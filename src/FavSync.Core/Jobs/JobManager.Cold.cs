using System.Diagnostics;
using FavSync.Core.Cold;
using FavSync.Core.Store;

namespace FavSync.Core.Jobs;

/// <summary>
/// 冷备份的作业编排（DESIGN-COLD-BACKUP.md §2）。
///
/// <para><b>一次作业循环打多桶</b>，而不是"一桶一作业"：200 多 GiB 要分几批打，
/// 一桶一作业得点 122 次。停止条件是 <c>maxGiB</c> / <c>maxBuckets</c> / 目标盘快满 / 没有未冷备 / 用户取消。
/// 每打完一桶立刻落库，所以取消只损失正在打的那一个桶（≤2 GiB）。</para>
/// </summary>
public sealed partial class JobManager
{
    private ColdService Cold() => new(_cfg, _db);

    /// <summary>
    /// 打桶。密码只在这次调用的参数里走一趟，**不落库、不写日志、不进 config**：
    /// 服务端唯一的用途就是把它转给 7z 进程。
    /// </summary>
    public string StartColdPack(string password, string label, string origin, int targetMiB,
        int maxBuckets, int maxGiB, bool verifyMd5)
    {
        // 开跑前把"能不能打"检查完：没配目录、没 7z，排进队列再报错只会让人等
        if (string.IsNullOrEmpty(password))
            throw new InvalidOperationException("必须设置压缩包密码：密码丢了这批桶就等于一堆随机字节");
        var svc = Cold();
        _ = svc.RequireColdRoot();
        if (svc.SevenZipExe is null) throw new InvalidOperationException(
            "没找到 7z：发布包里的 tools\\7z\\7za.exe 缺失时请重新完整解压，或在设置里指定 7z 路径");

        return Enqueue("cold", "冷备份", async (job, ct) =>
        {
            var sw = Stopwatch.StartNew();
            var service = Cold();
            var targetBytes = Math.Max(64, targetMiB) * 1024L * 1024L;

            // ---- 1. 现算未冷备（账驱动，不靠进度指针：删掉的桶自动排除）----
            job.Phase = "plan";
            job.Message = "正在清点未冷备条目…";
            Publish(job);

            var items = _db.UncoldItems();
            if (items.Count == 0)
            {
                job.State = "done";
                job.Message = "没有未冷备条目：库里的 primary 字节都已在桶里";
                job.EndedAt = DateTime.UtcNow;
                AddLog(job, job.Message);
                Publish(job);
                return;
            }

            job.Total = items.Count;
            var blocked = new List<ColdBlockedRow>();
            job.Message = $"账实核对 {items.Count} 条…";
            Publish(job);

            var ok = service.Inspect(items, verifyMd5, ct, (i, n) =>
            {
                job.Done = i;
                job.Message = $"账实核对 {i}/{n}（{(verifyMd5 ? "含 md5" : "大小")}）";
                Publish(job);
            }, blocked);

            _db.ReplaceBlocks(blocked);
            _db.SetMeta("lastCheckAt", DateTime.UtcNow.ToString("O"));
            if (blocked.Count > 0)
            {
                AddLog(job, $"⚠ {blocked.Count} 条账实不符，已踢出本次打包（面板「无法冷备」可看清单）");
                foreach (var b in blocked.Take(5)) AddLog(job, $"  · {b.Key}：{b.Reason}");
            }

            if (ok.Count == 0)
            {
                job.State = "failed";
                job.Message = "没有一条通过账实核对，无法打包";
                job.EndedAt = DateTime.UtcNow;
                Publish(job);
                return;
            }

            // ---- 2. 装箱（按 key 升序、装满即封、一条不跨桶）----
            var boxes = ColdService.Box(ok, targetBytes);
            if (maxBuckets > 0 && boxes.Count > maxBuckets) boxes = boxes.Take(maxBuckets).ToList();
            if (maxGiB > 0)
            {
                var cap = maxGiB * 1024L * 1024L * 1024L;
                long acc = 0;
                var cut = new List<List<ColdPlanItem>>();
                foreach (var box in boxes)
                {
                    var by = box.Sum(i => i.DiskBytes);
                    if (cut.Count > 0 && acc + by > cap) break;
                    cut.Add(box);
                    acc += by;
                }
                boxes = cut;
            }

            job.Total = boxes.Sum(b => b.Count);
            job.BytesTotal = boxes.Sum(b => b.Sum(i => i.DiskBytes));
            job.Done = 0;
            job.BytesDone = 0;
            job.Phase = "pack";
            AddLog(job, $"开始打包：{boxes.Count} 个桶 / {job.Total} 条 / {Fmt(job.BytesTotal)}"
                        + $"（桶大小 {targetMiB} MiB，{(verifyMd5 ? "md5 校验" : "只比大小")}）");

            var coldRoot = service.RequireColdRoot();
            long doneBytes = 0;
            var made = new List<string>();
            var stoppedByLimit = false;
            string? batchId = null;

            foreach (var box in boxes)
            {
                ct.ThrowIfCancellationRequested();

                var boxBytes = box.Sum(i => i.DiskBytes);
                var free = ColdService.FreeSpaceOf(coldRoot);
                if (free >= 0 && free < boxBytes + 512L * 1024 * 1024)
                {
                    stoppedByLimit = true;
                    AddLog(job, $"目标盘只剩 {Fmt(free)}，不够再打一桶（{Fmt(boxBytes)}）→ 停在这里，传完删掉再继续");
                    break;
                }

                job.Message = $"打包第 {made.Count + 1}/{boxes.Count} 桶（{box.Count} 条 / {Fmt(boxBytes)}）";
                Publish(job);

                var baseBytes = doneBytes;
                var row = await service.PackOneAsync(label, origin, targetMiB, box, password,
                    pct => job.BytesDone = baseBytes + boxBytes * pct / 100, ct).ConfigureAwait(false);

                doneBytes += row.Bytes;
                batchId = row.BatchId;
                made.Add(row.BucketId);
                // 桶文件比源字节略大（7z 头 + 块对齐），直接赋 doneBytes 会让进度冲到 100% 以上
                job.BytesDone = Math.Min(doneBytes, job.BytesTotal);
                job.Done += box.Count;
                AddLog(job, $"✓ {row.FileName}  {box.Count} 条 / {Fmt(row.Bytes)}  sha256 {row.Sha256![..12]}…");

                // 每打一桶就刷新清单：中断后已完成的部分仍然可读，不会"桶在、目录丢了"
                await service.WriteManifestAsync(row.BatchId, password, ct).ConfigureAwait(false);
                job.Message = $"已打 {made.Count}/{boxes.Count} 桶（{Fmt(doneBytes)}）";
                Publish(job);
            }

            if (batchId is not null)
            {
                _db.SetMeta("lastBatch", batchId);
                AddLog(job, $"清单：{service.ManifestPath(coldRoot, batchId)}（和桶一起上传，别漏）");
            }

            job.State = "done";
            job.Message = $"打包完成：{made.Count} 桶 / {job.Done} 条 / {Fmt(doneBytes)}"
                        + (stoppedByLimit ? "（按限额停下，传完删掉再继续）" : "")
                        + $"；{sw.Elapsed.TotalSeconds:F0}s";
            job.EndedAt = DateTime.UtcNow;
            AddLog(job, job.Message);
            Publish(job);
        });
    }

    /// <summary>
    /// 从冷备份恢复。<paramref name="bucketIds"/> 为空 = 清单里所有桶。
    ///
    /// <para>顺序是：读清单 → 清残渣 → <b>空间预检</b> → 逐桶（每桶内部再判一次该不该解包、
    /// 解多少）。预检放在这里而不是进桶之后，是为了"盘放不下"时立刻失败并说清差多少，
    /// 而不是搬了一半才发现。</para>
    /// </summary>
    public string StartColdRestore(string dir, string password, string[]? bucketIds)
    {
        if (string.IsNullOrEmpty(password))
            throw new InvalidOperationException("必须输入压缩包密码");

        dir = ColdService.NormDir(dir) ?? dir;

        return Enqueue("coldrestore", "从冷备份恢复", async (job, ct) =>
        {
            var sw = Stopwatch.StartNew();
            var service = Cold();

            job.Phase = "read";
            job.Message = "正在读清单…";
            Publish(job);

            var (mf, err) = await service.ReadManifestAsync(dir, password, ct).ConfigureAwait(false);
            if (mf is null) throw new InvalidOperationException(err ?? "读清单失败");

            var buckets = bucketIds is { Length: > 0 }
                ? mf.Buckets.Where(b => bucketIds.Contains(b.Id)).ToList()
                : mf.Buckets;

            // 上次中断留在 <库>/.favsync/restore-tmp 的解包残渣先清掉（那个目录只归恢复作业用）
            var cleaned = service.CleanRestoreTemp();
            if (cleaned > 0) AddLog(job, $"清掉上次中断留下的 {cleaned} 个解包临时目录");

            job.Total = buckets.Sum(b => b.Items.Count);
            job.BytesTotal = buckets.Sum(b => b.Items.Sum(i => i.Bytes > 0 ? i.Bytes : i.Files.Sum(f => f.Size)));

            // ── 空间预检（真跑之前的最后一道闸）──
            // 需要的空间 = 这次要落盘的字节（同卷 Move 不额外占）+ 一个桶的解包峰值 + 余量。
            var (needItems, needBytes) = service.EstimateRestore(buckets);
            var free = ColdService.FreeSpaceOf(_cfg.EffectiveArchiveRoot);
            if (needBytes > 0 && free >= 0)
            {
                var guard = needBytes + Math.Max(512L * 1024 * 1024, needBytes / 32);
                if (free < guard)
                {
                    job.Phase = "precheck";
                    throw new InvalidOperationException(
                        $"库盘放不下：这次要落盘约 {Fmt(needBytes)}（{needItems} 条），"
                        + $"{_cfg.EffectiveArchiveRoot} 只剩 {Fmt(free)}，还差 {Fmt(guard - free)}。"
                        + "先腾地方（或换库目录）再点恢复");
                }
                AddLog(job, $"空间预检通过：需落盘 {Fmt(needBytes)}（{needItems} 条），库盘剩 {Fmt(free)}");
            }
            else if (needBytes == 0)
            {
                AddLog(job, "这次没有需要搬的字节（清单里的条目本机都已有），只补账不解读包");
            }

            job.Phase = "restore";
            AddLog(job, $"清单 {mf.Batch}：{buckets.Count} 个桶 / {job.Total} 条 / {Fmt(job.BytesTotal)}");

            int restored = 0, skipped = 0, failed = 0, noSpace = 0, n = 0;
            long spaceSkippedBytes = 0;
            var notes = new List<string>();

            foreach (var b in buckets)
            {
                ct.ThrowIfCancellationRequested();
                job.Message = $"恢复第 {++n}/{buckets.Count} 桶（{b.Id}）";
                Publish(job);

                var baseBytes = job.BytesDone;
                var boxBytes = b.Items.Sum(i => i.Bytes > 0 ? i.Bytes : i.Files.Sum(f => f.Size));
                var noSpaceLast = false;
                try
                {
                    var r = await service.RestoreBucketAsync(dir, b.Id, password, null,
                        (i, total) => job.Done++, pct => job.BytesDone = baseBytes + boxBytes * pct / 100,
                        ct).ConfigureAwait(false);

                    restored += r.Restored;
                    skipped += r.Skipped;
                    failed += r.Failed;
                    notes.AddRange(r.Notes);
                    if (r.NoSpace)
                    {
                        noSpace++;
                        noSpaceLast = true;
                        spaceSkippedBytes += r.NeededBytes;
                        AddLog(job, $"⚠ {b.Id}：空间不足，整桶跳过（需 {Fmt(r.NeededBytes)}）");
                        foreach (var note in r.Notes) AddLog(job, "  · " + note);
                    }
                    else
                    {
                        AddLog(job, $"· {b.Id}：入库 {r.Restored}、跳过 {r.Skipped}、失败 {r.Failed}");
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failed += b.Items.Count;
                    notes.Add($"{b.Id}：{ex.Message}");
                    AddLog(job, $"✗ {b.Id}：{ex.Message}");
                }
                // 空间不足跳过的桶一个字节都没落盘，进度也就不能往前推
                job.BytesDone = Math.Min(baseBytes + (noSpaceLast ? 0 : boxBytes), job.BytesTotal);
                Publish(job);
            }

            foreach (var note in notes.Take(30)) AddLog(job, "  · " + note);
            if (notes.Count > 30) AddLog(job, $"  · 另有 {notes.Count - 30} 条未显示");

            job.State = "done";
            job.Message = $"恢复完成：入库 {restored} 条，跳过 {skipped}，失败 {failed}；{sw.Elapsed.TotalSeconds:F0}s"
                        + (noSpace > 0 ? $"（{noSpace} 个桶因空间不足未做，共 {Fmt(spaceSkippedBytes)}）" : "")
                        + (skipped > 0 ? "（跳过的多半是库里已有，不用管）" : "");
            job.EndedAt = DateTime.UtcNow;
            AddLog(job, job.Message);
            if (restored > 0) AddLog(job, "建议接着跑一次「库 → 扫描总库」复核账实，这是唯一能证明恢复完整的动作");
            Publish(job);
        });
    }

    private static string Fmt(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024 * 1024 => $"{b / 1024.0:F0} KB",
        < 1024L * 1024 * 1024 => $"{b / 1024.0 / 1024:F1} MB",
        _ => $"{b / 1024.0 / 1024 / 1024:F2} GB",
    };
}
