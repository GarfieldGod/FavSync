using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using FavSync.Core.Config;
using FavSync.Core.Scan;
using FavSync.Core.Store;

namespace FavSync.Core.Cold;

public sealed record ColdImportResult(int Buckets, int NewBuckets, int Items, int Probes,
    int ExpiredProbes, IReadOnlyList<string> Ids, string? Batch);

public sealed record ColdVerifyResult(bool Ok, string Message, long Bytes, string? Sha256);

/// <summary>
/// 冷备份的执行体：核对 → 装箱 → 打桶 → 写清单；以及反向的读清单 → 恢复。
///
/// <para><b>三条不变式（与总库铁律同源）</b>：</para>
/// <list type="number">
/// <item>打包前必须账实核对，不一致的条目踢出去，**不允许带病打包**（封进去的坏字节 = 把唯一副本标成安全）；</item>
/// <item>恢复必须走 <see cref="CatalogDatabase.UpsertLibraryItem"/> + <see cref="CatalogDatabase.RegisterBackup"/>，
/// 绝不写 <c>phone_*</c>、绝不覆盖已有字节；</item>
/// <item>永远先解到库外的临时目录，校验通过才 Move 进库 —— 中途失败不能在库里留半个条目。</item>
/// </list>
/// </summary>
public sealed class ColdService
{
    private readonly FavSyncConfig _cfg;
    private readonly CatalogDatabase _db;

    public ColdService(FavSyncConfig cfg, CatalogDatabase db)
    {
        _cfg = cfg;
        _db = db;
    }

    public string? SevenZipExe => SevenZip.Resolve(_cfg.SevenZipPath);

    private static string NoSevenZip =>
        "没找到 7z：发布包里的 tools\\7z\\7za.exe 缺失时请重新完整解压，或在设置里指定 7z 路径";

    /// <summary>冷备份根不存在时给出可以直接照做的提示，而不是抛 Win32 异常。</summary>
    public string RequireColdRoot()
    {
        var root = _cfg.EffectiveColdRoot;
        if (root is null)
            throw new InvalidOperationException("还没设置冷备份目录：设置页 →「冷备份与恢复」→ 冷备份目录");
        if (_cfg.ColdRootConflict(root) is { } conflict)
            throw new InvalidOperationException(conflict);
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>
    /// 路径规范化（<c>D://a//b</c> → <c>D:\a\b</c>）。
    /// 桶的 <c>dir</c> 会落库并在面板显示，还会参与"文件在不在冷备目录里"的比较，
    /// 所以入口处必须规整一次，别把前端传来的写法原样存进去。
    /// </summary>
    public static string? NormDir(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return dir;
        try { return Path.GetFullPath(dir); } catch { return dir; }
    }

    // ── 桶文件存在性缓存 ──
    // /cold/status 要给每个桶判一次 File.Exists。桶可能躺在网盘挂载目录上
    // （实测：44 次 stat 花掉 28 秒，页面直接超时），
    // 而"桶文件在不在"这件事几秒内不会变，缓 30 秒足够。
    private static readonly object ProbeGate = new();
    private static readonly Dictionary<string, (bool Exists, DateTime At)> ProbeCache =
        new(StringComparer.OrdinalIgnoreCase);

    public static bool ExistsCached(string path, int ttlSeconds = 30)
    {
        lock (ProbeGate)
        {
            if (ProbeCache.TryGetValue(path, out var hit)
                && (DateTime.UtcNow - hit.At).TotalSeconds < ttlSeconds)
                return hit.Exists;
        }

        bool exists;
        try { exists = File.Exists(path); } catch { exists = false; }

        lock (ProbeGate) ProbeCache[path] = (exists, DateTime.UtcNow);
        return exists;
    }

    /// <summary>桶状态/文件变动后立刻失效（标记上传、删本地、打包完）。</summary>
    public static void InvalidateProbeCache()
    {
        lock (ProbeGate) ProbeCache.Clear();
    }

    // ───────────────────────── 预估 ─────────────────────────

    /// <summary>
    /// 只读预估：条目数与字节数走账、只 <c>Directory.Exists</c> 粗筛一遍文件在不在。
    /// 真正的账实核对（逐文件 stat）在打包时做 —— 那一步才可能改变结果，所以这里的数字是"预计"。
    /// </summary>
    public ColdEstimate Estimate(int targetMiB, CancellationToken ct)
    {
        var root = _cfg.EffectiveArchiveRoot;
        var items = _db.UncoldItems();
        var ok = new List<ColdPlanItem>();
        long bytes = 0, blockedBytes = 0;
        var sample = new List<string>();

        foreach (var it in items)
        {
            ct.ThrowIfCancellationRequested();
            var dir = Path.Combine(root, it.RelDir.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(dir))
            {
                blockedBytes += DiskSizeOfBackup(it);
                if (sample.Count < 8) sample.Add($"{it.Key}：目录不在 {it.RelDir}");
                continue;
            }
            var size = DiskSizeOfBackup(it);
            bytes += size;
            ok.Add(it);
        }

        var target = Math.Max(64, targetMiB) * 1024L * 1024L;
        return new ColdEstimate(ok.Count, bytes, CountBuckets(ok, target),
            items.Count - ok.Count, blockedBytes, targetMiB, sample);
    }

    /// <summary>
    /// 恢复前预检：这批桶真正要落盘多少字节（本机已有同一份三元组的条目不算）。
    /// 纯查账，不碰磁盘 —— 预检本身必须在几秒内出结果。
    /// </summary>
    public (int Items, long Bytes) EstimateRestore(IEnumerable<ColdManifestBucket> buckets)
    {
        int n = 0;
        long by = 0;
        foreach (var b in buckets)
        foreach (var it in b.Items)
        {
            if (_db.BackupExists(it.Key, it.TagNorm, it.ContentId)) continue;
            n++;
            by += BytesOf(it);
        }
        return (n, by);
    }

    /// <summary>
    /// 账目上这份字节有多大（video+audio，含元数据大约 1% 余量）。
    /// 预估阶段用它，打包阶段用磁盘实测值。
    /// </summary>
    private static long DiskSizeOfBackup(ColdPlanItem it) =>
        it.VideoSize + it.AudioSize > 0
            ? (long)((it.VideoSize + it.AudioSize) * 1.02) + 64 * 1024
            : 0;

    private static int CountBuckets(IReadOnlyList<ColdPlanItem> items, long target)
    {
        int n = 0;
        long cur = 0;
        foreach (var it in items)
        {
            var size = Math.Max(0, DiskSizeOfBackup(it));
            if (n > 0 && cur + size > target) { n++; cur = 0; }
            else if (n == 0) n = 1;
            cur += size;
        }
        return n;
    }

    // ───────────────────────── 账实核对 + 文件集 ─────────────────────────

    /// <summary>
    /// 逐条核对磁盘：目录在不在、video/audio 的实际字节数对不对（= 扫库那套 L2 规则），
    /// 同时把**真实文件集**收集出来。返回通过的条目，不通过的写进 <c>blocked</c>。
    /// </summary>
    public List<ColdPlanItem> Inspect(IEnumerable<ColdPlanItem> items, bool verifyMd5,
        CancellationToken ct, Action<int, int>? progress, List<ColdBlockedRow> blocked)
    {
        var root = _cfg.EffectiveArchiveRoot;
        var ok = new List<ColdPlanItem>();
        var list = items.ToList();
        var now = DateTime.UtcNow.ToString("O");
        var i = 0;

        foreach (var it in list)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke(++i, list.Count);

            var dir = Path.Combine(root, it.RelDir.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(dir))
            {
                blocked.Add(new ColdBlockedRow(it.Key, it.TagNorm, it.ContentId,
                    "条目目录不存在（账上有、盘上无）", now, it.Title));
                continue;
            }

            it.Files.Clear();
            foreach (var f in EnumerateFiles(dir))
                it.Files.Add(f);

            if (it.Files.Count == 0)
            {
                blocked.Add(new ColdBlockedRow(it.Key, it.TagNorm, it.ContentId,
                    "目录是空的（一个文件都没有）", now, it.Title));
                continue;
            }

            var issue = CheckStreams(it, verifyMd5, dir);
            if (issue is not null)
            {
                blocked.Add(new ColdBlockedRow(it.Key, it.TagNorm, it.ContentId, issue, now, it.Title));
                continue;
            }
            ok.Add(it);        }

        return ok;
    }

    private static IEnumerable<ColdFile> EnumerateFiles(string dir)
    {
        IEnumerable<string> all;
        try { all = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories); }
        catch { yield break; }

        foreach (var path in all)
        {
            FileInfo fi;
            try { fi = new FileInfo(path); } catch { continue; }
            if (!fi.Exists) continue;
            var rel = Path.GetRelativePath(dir, path).Replace('\\', '/');
            yield return new ColdFile { Name = rel, Role = Roles.FromName(rel), Size = fi.Length };
        }
    }

    /// <summary>
    /// 流级核对：<c>&lt;rel_dir&gt;/&lt;tag&gt;/video.m4s</c> 与 audio.m4s 的实际字节必须等于账目。
    /// 这条规则与「扫描总库」的账实核对完全一致（同一个判据，两处不能各说一套）。
    /// </summary>
    private static string? CheckStreams(ColdPlanItem it, bool verifyMd5, string dir)
    {
        string Want(string what) => $"{it.TagRaw}/{what}.m4s";

        var v = it.Files.FirstOrDefault(f => f.Name == Want("video"));
        var a = it.Files.FirstOrDefault(f => f.Name == Want("audio"));

        // index.json 自带每个流的 size + md5：顺手抄进文件集。
        // 这样清单拿到一台没有 catalog 的新机器上，仍能对 m4s 做逐字节校验（DESIGN-COLD-BACKUP.md §5 的等价性）。
        var (idxV, idxA) = ReadIndexMd5(dir, it.TagRaw);
        if (v is not null && !string.IsNullOrEmpty(idxV)) v.Md5 = idxV;
        if (a is not null && !string.IsNullOrEmpty(idxA)) a.Md5 = idxA;

        if (it.VideoSize > 0)
        {
            if (v is null) return $"缺 {Want("video")}（账目 {Fmt(it.VideoSize)}）";
            if (v.Size != it.VideoSize)
                return $"video 实际 {Fmt(v.Size)}，账目 {Fmt(it.VideoSize)}（差 {Fmt(it.VideoSize - v.Size)}）";
        }
        if (it.AudioSize > 0)
        {
            if (a is null) return $"缺 {Want("audio")}（账目 {Fmt(it.AudioSize)}）";
            if (a.Size != it.AudioSize)
                return $"audio 实际 {Fmt(a.Size)}，账目 {Fmt(it.AudioSize)}（差 {Fmt(it.AudioSize - a.Size)}）";
        }

        if (!verifyMd5) return null;

        foreach (var (file, md5) in new[] { (v, it.VideoMd5), (a, it.AudioMd5) })
        {
            if (file is null || string.IsNullOrEmpty(md5)) continue;
            var path = Path.Combine(dir, file.Name.Replace('/', Path.DirectorySeparatorChar));
            string actual;
            try { actual = Md5File(path); }
            catch (Exception ex) { return $"算 md5 失败：{ex.Message}"; }
            if (!string.Equals(actual, md5, StringComparison.OrdinalIgnoreCase))
                return $"{file.Name} md5 不符：实际 {actual[..8]}…，账目 {md5[..Math.Min(8, md5.Length)]}…";
        }
        return null;
    }

    /// <summary>读 <c>&lt;rel_dir&gt;/&lt;tag&gt;/index.json</c> 里 video / audio 的 md5。没有就返回 null。</summary>
    private static (string? Video, string? Audio) ReadIndexMd5(string dir, string tagRaw)
    {
        var path = Path.Combine(dir, tagRaw, "index.json");
        if (!File.Exists(path)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            static string? Pick(JsonDocument d, string name)
            {
                if (!d.RootElement.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
                    return null;
                foreach (var e in arr.EnumerateArray())
                    if (e.TryGetProperty("md5", out var md5) && md5.ValueKind == JsonValueKind.String)
                        return md5.GetString();
                return null;
            }
            return (Pick(doc, "video"), Pick(doc, "audio"));
        }
        catch { return (null, null); }
    }

    private static string Md5File(string path)    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1 << 20, FileOptions.SequentialScan);
        using var md5 = System.Security.Cryptography.MD5.Create();
        return Convert.ToHexString(md5.ComputeHash(fs)).ToLowerInvariant();
    }

    private static string Fmt(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024 * 1024 => $"{b / 1024.0:F0} KB",
        < 1024L * 1024 * 1024 => $"{b / 1024.0 / 1024:F1} MB",
        _ => $"{b / 1024.0 / 1024 / 1024:F2} GB",
    };

    // ───────────────────────── 装箱 ─────────────────────────

    /// <summary>
    /// 贪心装箱：按 key 升序、装满即封、**一条不跨桶**。
    /// 顺序固定就意味着同一批条目两次打包的封桶结果一致，"继续"不会重打。
    /// 单条比桶还大时独占一桶（少量超目标，面板按实际字节显示）。
    /// </summary>
    public static List<List<ColdPlanItem>> Box(IReadOnlyList<ColdPlanItem> items, long targetBytes)
    {
        var buckets = new List<List<ColdPlanItem>>();
        var cur = new List<ColdPlanItem>();
        long curBytes = 0;
        long target = Math.Max(64L * 1024 * 1024, targetBytes);

        foreach (var it in items.OrderBy(i => i.Key, StringComparer.Ordinal).ThenBy(i => i.TagNorm, StringComparer.Ordinal))
        {
            var size = it.DiskBytes;
            if (cur.Count > 0 && curBytes + size > target)
            {
                buckets.Add(cur);
                cur = new List<ColdPlanItem>();
                curBytes = 0;
            }
            cur.Add(it);
            curBytes += size;
        }
        if (cur.Count > 0) buckets.Add(cur);
        return buckets;
    }

    // ───────────────────────── 打一个桶 ─────────────────────────

    /// <summary>
    /// 打一个桶（同步阻塞在 7z 上）。成功后<b>立刻</b>落账：<c>cold_item</c> 写入 + 桶置 <c>packed</c>，
    /// 同一个事务。所以取消/断电最多损失这一个桶（≤2 GiB），续打靠账自动跳过已打的。
    /// </summary>
    public async Task<ColdBucketRow> PackOneAsync(string label, string origin, int targetMiB,
        IReadOnlyList<ColdPlanItem> items, string password,
        Action<int>? onPercent, CancellationToken ct)
    {
        var exe = SevenZipExe ?? throw new InvalidOperationException(NoSevenZip);
        var coldRoot = RequireColdRoot();

        var batchId = BatchId(label, origin);
        var seq = _db.NextColdSeq(origin);
        var bucketId = $"{batchId}-{seq:0000}";
        var fileName = bucketId + ".7z";
        var archivePath = Path.Combine(coldRoot, fileName);

        var est = items.Sum(i => i.DiskBytes);
        var free = FreeSpace(coldRoot);
        if (free >= 0 && free < est + 512L * 1024 * 1024)
            throw new InvalidOperationException(
                $"冷备份盘空间不够：这一桶约 {Fmt(est)}，只剩 {Fmt(free)}（换一块盘或把桶大小调小）");

        var row = new ColdBucketRow(bucketId, batchId, label, seq, origin, fileName, 0, null,
            items.Count, targetMiB, ColdState.Packing, null, DateTime.UtcNow.ToString("O"),
            null, null, null);
        _db.InsertBucket(row);

        // 7z 的 @list 文件：内容就是 rel_dir，工作目录设成总库根 —— 归档里因此天然是
        // <avid>/c_<cid>/... 的结构，解到任意根目录就能直接还原库布局（不需要 --strip）
        var listFile = Path.Combine(Path.GetTempPath(), $"favsync-cold-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllLinesAsync(listFile,
                items.Select(i => i.RelDir).Distinct(StringComparer.Ordinal), ct).ConfigureAwait(false);

            var r = await SevenZip.CreateAsync(exe, archivePath, listFile, password,
                _cfg.EffectiveArchiveRoot, onPercent, ct).ConfigureAwait(false);

            if (!r.Ok)
            {
                TryDelete(archivePath);
                _db.SetBucketState(bucketId, ColdState.Aborted, "7z 失败：" + Tail(r.Output));
                throw new InvalidOperationException("7z 打包失败：" + Tail(r.Output));
            }

            var bytes = new FileInfo(archivePath).Length;
            var sha = SevenZip.Sha256File(archivePath, ct);   // 写回读一遍：把"写盘写坏了"挡在这里

            _db.CommitPackedBucket(bucketId, bytes, sha,
                items.Select(i => (i.Key, i.TagNorm, i.ContentId, i.DiskBytes, Sha256OfItem(i))).ToList());

            return _db.GetBucket(bucketId)!;
        }
        catch (OperationCanceledException)
        {
            // 取消 = 这一桶作废，桶号保留不复用，条目退回未冷备（cold_item 本来就没写）
            TryDelete(archivePath);
            _db.SetBucketState(bucketId, ColdState.Aborted, "取消");
            throw;
        }
        finally
        {
            TryDelete(listFile);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static long FreeSpace(string dir)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!).AvailableFreeSpace; }
        catch { return -1; }
    }

    /// <summary>供作业侧判断"该不该再打下一桶"（目标盘快满就体面地停下，而不是打到一半抛异常）。</summary>
    public static long FreeSpaceOf(string dir) => FreeSpace(dir);

    private static string Tail(string? s, int n = 400)
    {
        if (string.IsNullOrWhiteSpace(s)) return "（无输出）";
        s = s.Trim();
        return s.Length <= n ? s : "…" + s[^n..];
    }

    /// <summary>
    /// 条目内容指纹：文件集（名字 + 字节）的 sha256。不用 md5 是因为它要把 234 GiB 全读一遍，
    /// 而这里只需要"这一条在清单里有没有变"，用文件集摘要就够，还顺手把文件集改动也纳入。
    /// </summary>
    public static string Sha256OfItem(ColdPlanItem it)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var f in it.Files.OrderBy(f => f.Name, StringComparer.Ordinal))
            sb.Append(f.Name).Append('|').Append(f.Role).Append('|').Append(f.Size).Append('\n');
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string BatchId(string label, string origin) =>
        $"{Sanitize(label)}-{Sanitize(origin)}-{DateTime.Now:yyyyMMdd}";

    private static string Sanitize(string s)
    {
        var clean = new string((s ?? "").Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.').ToArray());
        return string.IsNullOrEmpty(clean) ? "cold" : clean;
    }

    // ───────────────────────── 清单 ─────────────────────────

    public string ManifestPath(string coldRoot, string batchId) =>
        Path.Combine(coldRoot, batchId + ".manifest.7z");

    /// <summary>
    /// 重写本批次的清单包。**每打完一个桶就刷一遍**（约 800 KB）：中断后已完成的部分仍然可读，
    /// 不会出现"桶在、目录丢了"。
    /// </summary>
    public async Task WriteManifestAsync(string batchId, string password, CancellationToken ct)
    {
        var exe = SevenZipExe ?? throw new InvalidOperationException(NoSevenZip);
        var coldRoot = RequireColdRoot();
        var buckets = _db.BucketsOfBatch(batchId);
        if (buckets.Count == 0) return;

        var first = buckets[0];
        var manifest = new ColdManifest
        {
            Batch = batchId,
            Label = first.Label,
            Origin = first.Origin,
            CreatedAt = first.CreatedAt,
            UpdatedAt = DateTime.UtcNow.ToString("O"),
            BucketMiB = first.TargetMiB,
            Ledger = new ColdLedger { Probes = _db.ProbeLedger() },
        };

        foreach (var b in buckets)
        {
            var mfBucket = new ColdManifestBucket
            {
                Id = b.BucketId,
                File = b.FileName,
                Bytes = b.Bytes,
                Sha256 = b.Sha256,
                State = b.State,
            };

            foreach (var t in _db.ColdItemsOfBucket(b.BucketId))
                if (ManifestItem(t.Key, t.TagNorm, t.ContentId, t.Bytes, t.Sha) is { } mi)
                    mfBucket.Items.Add(mi);

            manifest.Buckets.Add(mfBucket);
        }

        var json = JsonSerializer.Serialize(manifest, ColdJson.Options);
        var work = Path.Combine(Path.GetTempPath(), "favsync-manifest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var jsonPath = Path.Combine(work, "manifest.json");
        var outPath = ManifestPath(coldRoot, batchId);
        try
        {
            await File.WriteAllTextAsync(jsonPath, json, ct).ConfigureAwait(false);
            TryDelete(outPath);
            var r = await SevenZip.RunAsync(exe, new[]
            {
                "a", "-t7z", "-mx5", "-ms=off", "-mhe=on", "-y", "-p" + password,
                outPath, "manifest.json",
            }, work, null, ct).ConfigureAwait(false);
            if (!r.Ok) throw new InvalidOperationException("写清单失败：" + Tail(r.Output));
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { }
        }
    }

    /// <summary>
    /// 清单里的一条。<b>字节口径一律取自 <c>backup</c> 行</b>（那份真的被封进桶的数据），
    /// 不是 <c>item.phone_*</c>（手机上的那份）。两者在 outdated / 换源之后不同，
    /// 混用会让"恢复时重算三元组"算出另一个键 —— 详见 <see cref="CatalogDatabase.ColdManifestSource"/>。
    /// </summary>
    private ColdManifestItem? ManifestItem(string key, string tagNorm, string contentId,
        long bytes, string sha)
    {
        var meta = _db.ColdManifestSource(key, tagNorm, contentId);
        if (meta is null) return null;

        return new ColdManifestItem
        {
            Key = key,
            Avid = meta.Avid,
            Cid = meta.Cid,
            RelDir = meta.RelDir,
            Tag = meta.TagRaw,
            TagNorm = tagNorm,
            ContentId = contentId,
            Quality = meta.Quality,
            Codecid = meta.Codecid,
            Title = meta.Title,
            Owner = meta.Owner,
            OwnerMid = meta.OwnerMid,
            Bvid = meta.Bvid,
            Cover = meta.Cover,
            Page = meta.Page,
            Width = meta.Width,
            Height = meta.Height,
            DurationMs = meta.DurationMs,
            VideoSize = meta.VideoSize,
            VideoMd5 = meta.VideoMd5,
            AudioSize = meta.AudioSize,
            AudioMd5 = meta.AudioMd5,
            Source = "cold",
            BackupCreatedAt = meta.SyncedAt,
            Bytes = bytes,
            Sha256 = sha,
            Files = DiskFiles(meta.RelDir),
        };
    }

    private List<ColdManifestFile> DiskFiles(string relDir)
    {
        var dir = Path.Combine(_cfg.EffectiveArchiveRoot, relDir.Replace('/', Path.DirectorySeparatorChar));
        var list = new List<ColdManifestFile>();
        if (!Directory.Exists(dir)) return list;
        foreach (var f in EnumerateFiles(dir))
            list.Add(new ColdManifestFile { Name = f.Name, Role = f.Role, Size = f.Size, Md5 = f.Md5 });
        return list;
    }

    // ───────────────────────── 读清单 / 预览 / 导入 ─────────────────────────

    /// <summary>
    /// 从任意目录读清单（恢复向导第一步）。<b>密码错立刻能区分</b>：
    /// <c>-mhe=on</c> 下错密码连文件名都列不出来，7z 直接非 0 退出 —— 不需要自己造校验器。
    /// </summary>
    public async Task<(ColdManifest? Manifest, string? Error)> ReadManifestAsync(
        string dir, string password, CancellationToken ct)
    {
        var exe = SevenZipExe ?? throw new InvalidOperationException(NoSevenZip);
        if (!Directory.Exists(dir)) return (null, "目录不存在：" + dir);

        var file = Directory.EnumerateFiles(dir, "*.manifest.7z").OrderByDescending(f => f).FirstOrDefault();
        if (file is null) return (null, "这个目录里没有清单文件（*.manifest.7z）。清单必须和桶一起拷过来");

        var work = Path.Combine(Path.GetTempPath(), "favsync-read-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var r = await SevenZip.ExtractAsync(exe, file, work, password, null, ct).ConfigureAwait(false);
            if (!r.Ok)
                return (null, SevenZip.LooksLikeWrongPassword(r)
                    ? "密码不对（清单解不开）"
                    : "读清单失败：" + Tail(r.Output));

            var json = Path.Combine(work, "manifest.json");
            if (!File.Exists(json)) return (null, "清单包里没有 manifest.json（文件损坏？）");
            var text = await File.ReadAllTextAsync(json, ct).ConfigureAwait(false);
            var mf = JsonSerializer.Deserialize<ColdManifest>(text, ColdJson.Options);
            return mf is null ? (null, "清单内容无法解析") : (mf, null);
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { }
        }
    }

    /// <summary>清单里的条目与本机库对账：本机缺 / 已有 / 有其它档位（DESIGN-COLD-BACKUP.md §7 的三类表）。</summary>
    public ColdPreview Preview(ColdManifest mf, string dir)
    {
        var items = new List<ColdRestoreItem>();
        var bucketIds = new List<string>();
        var bytes = 0L;

        foreach (var b in mf.Buckets)
        {
            bucketIds.Add(b.Id);
            foreach (var it in b.Items)
            {
                var rec = new ColdRestoreItem
                {
                    Key = it.Key,
                    TagNorm = it.TagNorm,
                    ContentId = it.ContentId,
                    BucketId = b.Id,
                    Title = it.Title,
                    Owner = it.Owner,
                    Tag = it.Tag,
                    Quality = it.Quality,
                    Bytes = it.Files.Sum(f => f.Size),
                };
                bytes += rec.Bytes;

                if (_db.BackupExists(it.Key, it.TagNorm, it.ContentId))
                {
                    rec.Reconcile = "exists";
                    rec.ReconcileNote = "本机已有同一份字节，跳过";
                }
                else if (_db.HasAnyBackup(it.Key))
                {
                    rec.Reconcile = "other";
                    rec.ReconcileNote = "本机有该视频的另一个档位，恢复后会并存为第二份";
                }
                else
                {
                    rec.Reconcile = "missing";
                    rec.ReconcileNote = _db.ItemRowExists(it.Key) ? "库里有条目、缺这份字节" : "本机库里没有这个条目";
                }
                items.Add(rec);
            }
        }

        return new ColdPreview(dir, mf.Batch, mf.Buckets.Count, items.Count, bytes,
            bucketIds, items,
            items.Count(i => i.Reconcile == "missing"),
            items.Count(i => i.Reconcile == "exists"),
            items.Count(i => i.Reconcile == "other"), null);
    }

    /// <summary>
    /// 导入外来桶（跨机搬运的关键一步）：登记桶 + 挂上条目 + 补 probe 结论。
    /// <b>不搬字节、不改库</b>，所以可以随便跑；跑完本机就知道"这些已经冷备过，别再打一遍"。
    /// </summary>
    public async Task<ColdImportResult> ImportAsync(string dir, string password, CancellationToken ct)
    {
        dir = NormDir(dir) ?? dir;
        var (mf, err) = await ReadManifestAsync(dir, password, ct).ConfigureAwait(false);
        if (mf is null) throw new InvalidOperationException(err ?? "读清单失败");

        int newBuckets = 0, items = 0;
        var ids = new List<string>();
        foreach (var b in mf.Buckets)
        {
            ids.Add(b.Id);
            var isNew = !_db.BucketExists(b.Id);
            if (isNew)
            {
                _db.InsertBucket(new ColdBucketRow(b.Id, mf.Batch, mf.Label,
                    SeqOf(b.Id), mf.Origin, b.File, b.Bytes, b.Sha256, b.Items.Count,
                    mf.BucketMiB, StateOrPacked(b.State), "导入（文件在 " + dir + "）",
                    mf.CreatedAt, mf.UpdatedAt, null, dir));
                newBuckets++;
            }

            items += _db.ImportColdItems(b.Id, b.Items.Select(i => (
                i.Key, i.TagNorm, i.ContentId,
                i.Bytes > 0 ? i.Bytes : i.Files.Sum(f => f.Size),
                string.IsNullOrEmpty(i.Sha256) ? i.ContentId : i.Sha256)).ToList());
        }

        int probes = 0, expired = 0;
        if (mf.Ledger is { Probes.Count: > 0 })
        {
            var (imp, _, exp) = _db.ImportProbeLedger(mf.Ledger.Probes);
            probes = imp;
            expired = exp;
        }

        _db.SetMeta("lastBatch", mf.Batch);
        return new ColdImportResult(mf.Buckets.Count, newBuckets, items, probes, expired, ids, mf.Batch);
    }

    private static int SeqOf(string id)
    {
        var tail = id.Split('-').LastOrDefault() ?? "0";
        return int.TryParse(tail, out var n) ? n : 0;
    }

    // ───────────────────────── 恢复 ─────────────────────────

    /// <summary>一条恢复动作。先算出整桶要做哪些事，再决定"要不要解包 / 解多少"。</summary>
    private enum RestoreAction
    {
        /// <summary>真的要搬字节。</summary>
        Move,
        /// <summary>本机已有同一份字节（三元组相同）。</summary>
        SkipExists,
        /// <summary>本机有更高的档位，这份按 RegisterBackup 的规则会被拒 —— 干脆不搬。</summary>
        SkipLower,
        /// <summary>目标目录已经存在（总库只增不删，绝不覆盖）。</summary>
        SkipDir,
    }

    private sealed record RestoreStep(ColdManifestItem Item, RestoreAction Action, string DstRel, string? Note);

    public sealed record ColdRestoreResult(int Restored, int Skipped, int Failed, bool NoSpace,
        long NeededBytes, IReadOnlyList<string> Notes);

    private static long BytesOf(ColdManifestItem it) => it.Bytes > 0 ? it.Bytes : it.Files.Sum(f => f.Size);

    /// <summary>
    /// 先把整桶要做的事算清楚（纯查库 + 几次 <c>Directory.Exists</c>）。
    /// <para><b>为什么要先算</b>：7z 是整桶解压，一条都不用搬的桶不该被摊开 ——
    /// 44 个桶全是"已恢复过"的状态时，白解 84 GiB 纯属浪费；一条只要 300 MB 的桶
    /// 也不该占 2 GiB 的临时空间。</para>
    /// </summary>
    private List<RestoreStep> PlanRestore(ColdManifestBucket bucket, IReadOnlyCollection<string>? onlyKeys)
    {
        var root = _cfg.EffectiveArchiveRoot;
        var steps = new List<RestoreStep>();

        foreach (var it in bucket.Items)
        {
            if (onlyKeys is not null && !onlyKeys.Contains(it.Key)) continue;

            if (_db.BackupExists(it.Key, it.TagNorm, it.ContentId))
            {
                steps.Add(new RestoreStep(it, RestoreAction.SkipExists, it.RelDir, null));
                continue;
            }

            var others = _db.BackupsOfKey(it.Key);

            // 与 RegisterBackup 同一条判据（QualityRank：先比 q，q 相同比编码）。
            // 库里已有副本 → 一律不搬字节，账照样记（桶里确实有这份）。
            // 一个条目只保留一份字节：恢复不覆盖库里的主副本
            // （"谁更好"由用户在「冲突」里决定），桶里这份留着当冷备。
            if (others.Count > 0)
            {
                var b = others.OrderByDescending(o => o.Quality ?? 0).First();
                steps.Add(new RestoreStep(it, RestoreAction.SkipLower, it.RelDir,
                    $"库里已有这份条目的副本（q{b.Quality}/{QualityRank.CodecName(b.Codecid)}），" +
                    $"恢复不覆盖；桶里这份留着当冷备（账照样记）"));
                continue;
            }

            // 落盘位置只有镜像路径 <avid>/c_<cid>/，不再有 .versions 长期仓库
            var dstRel = it.RelDir;
            var dst = Path.Combine(root, dstRel.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(dst))
            {
                steps.Add(new RestoreStep(it, RestoreAction.SkipDir, dstRel,
                    "库里已有同名目录，未覆盖（可跑一次「扫描总库」认领）"));
                continue;
            }

            steps.Add(new RestoreStep(it, RestoreAction.Move, dstRel, null));
        }
        return steps;
    }

    /// <summary>
    /// 从一个桶里恢复指定条目。守则：
    /// <list type="number">
    /// <item><b>先算后做</b>：一条都不用搬就完全不解包；只搬一部分就只解那几个 rel_dir；</item>
    /// <item><b>先查空间</b>：临时目录在库盘上，解压峰值必须放得下，否则整桶跳过而不是搬一半；</item>
    /// <item>先验归档 sha256、解到库外临时目录、逐文件核对大小后才 Move 进库；</item>
    /// <item>账以库为准：三元组用 <see cref="CatalogDatabase.RegisterBackup"/> 真正写进去的那一组。</item>
    /// </list>
    /// </summary>
    public async Task<ColdRestoreResult> RestoreBucketAsync(
        string dir, string bucketId, string password,
        IReadOnlyCollection<string>? onlyKeys,
        Action<int, int>? onItem, Action<int>? onPercent, CancellationToken ct)
    {
        var exe = SevenZipExe ?? throw new InvalidOperationException(NoSevenZip);
        var notes = new List<string>();

        dir = NormDir(dir) ?? dir;
        var (mf, err) = await ReadManifestAsync(dir, password, ct).ConfigureAwait(false);
        if (mf is null) throw new InvalidOperationException(err ?? "读清单失败");

        var bucket = mf.Buckets.FirstOrDefault(b => b.Id == bucketId)
                     ?? throw new InvalidOperationException($"清单里没有桶 {bucketId}");

        var archive = Path.Combine(dir, bucket.File);
        if (!File.Exists(archive)) throw new InvalidOperationException($"桶文件不在：{archive}");

        if (!string.IsNullOrEmpty(bucket.Sha256))
        {
            var actual = SevenZip.Sha256File(archive, ct);
            if (!string.Equals(actual, bucket.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("桶文件 sha256 与清单不符（传输损坏或不是同一个文件）");
        }

        // 恢复的同时就把"这份字节在哪个桶里"认下来（恢复 = 桶确实存在过，账必须跟着走）。
        EnsureBucketRegistered(mf, bucket, dir);

        var steps = PlanRestore(bucket, onlyKeys);
        var need = steps.Where(s => s.Action == RestoreAction.Move).ToList();
        var neededBytes = need.Sum(s => BytesOf(s.Item));
        int restored = 0, skipped = 0, failed = 0, i2 = 0;

        // ── 一条都不用搬：不解包，只补账 ──
        if (need.Count == 0)
        {
            foreach (var s in steps)
            {
                onItem?.Invoke(++i2, steps.Count);
                skipped++;
                RecordColdCoverage(bucketId, s.Item);
                if (s.Note is not null) notes.Add($"{s.Item.Key}：{s.Note}");
            }
            return new ColdRestoreResult(0, skipped, 0, false, 0, notes);
        }

        // ── 空间预检：临时解包目录在库盘上（同卷才能 Move），峰值 = 待解出的字节 ──
        // -mx0 不压缩，解压体积≈原件；再留 3% + 512 MB 给 7z 头、目录项与文件系统碎片。
        var free = FreeSpace(_cfg.EffectiveArchiveRoot);
        var guard = neededBytes + Math.Max(512L * 1024 * 1024, neededBytes / 32);
        if (free >= 0 && free < guard)
        {
            notes.Add($"空间不足：本桶要解出 {Fmt(neededBytes)}，库盘只剩 {Fmt(free)}"
                      + $"（还差 {Fmt(guard - free)}）→ 整桶跳过，先腾地方再恢复");
            return new ColdRestoreResult(0, steps.Count, 0, true, neededBytes, notes);
        }

        // 临时目录必须落在总库<b>同一个卷</b>上：Directory.Move 不能跨卷。
        // 解包到 <系统临时目录>、目标是总库（两者不同卷）会 Move 失败：
        // 1894 条全部 "Move 入库失败 … will not work across volumes"，22 分钟白跑，
        // 还在总库根留下 1816 个空 avid 目录。放在 <库>/.favsync 下既不跨卷也不被扫库看见。
        var tempRoot = Path.Combine(_cfg.EffectiveArchiveRoot, ".favsync", "restore-tmp");
        Directory.CreateDirectory(tempRoot);
        var temp = Path.Combine(tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            // 只解需要的那几个 rel_dir；全都要就整桶解（省掉过滤开销）
            var include = need.Count == steps.Count
                ? null
                : need.Select(s => s.Item.RelDir).Distinct(StringComparer.Ordinal).ToList();

            var r = await SevenZip.ExtractAsync(exe, archive, temp, password, onPercent, ct, include)
                .ConfigureAwait(false);

            // 7z 的 -i! 过滤在某些路径写法下会"一条都没匹配上"（退出码 1）：
            // 这不是数据坏了，退一步整桶解，别把用户的恢复卡死在这里
            if (!r.Ok && include is not null)
            {
                notes.Add($"{bucketId}：子集解包失败（{Tail(r.Output, 120)}），改整桶解");
                r = await SevenZip.ExtractAsync(exe, archive, temp, password, onPercent, ct)
                    .ConfigureAwait(false);
            }
            if (!r.Ok) throw new InvalidOperationException("解包失败：" + Tail(r.Output));

            foreach (var s in steps)
            {
                ct.ThrowIfCancellationRequested();
                onItem?.Invoke(++i2, steps.Count);

                var it = s.Item;
                if (s.Action != RestoreAction.Move)
                {
                    skipped++;
                    RecordColdCoverage(bucketId, it);
                    if (s.Note is not null) notes.Add($"{it.Key}：{s.Note}");
                    continue;
                }

                var src = Path.Combine(temp, it.RelDir.Replace('/', Path.DirectorySeparatorChar));
                var dst = Path.Combine(_cfg.EffectiveArchiveRoot,
                    s.DstRel.Replace('/', Path.DirectorySeparatorChar));

                if (!Directory.Exists(src))
                {
                    failed++;
                    notes.Add($"{it.Key}：解出来的目录不在（{it.RelDir}）");
                    continue;
                }

                var bad = VerifyExtracted(src, it);
                if (bad is not null)
                {
                    failed++;
                    notes.Add($"{it.Key}：{bad}");
                    continue;
                }

                if (Directory.Exists(dst))
                {
                    // 总库只增不删不覆盖：目标已存在就不动它，交给扫库认领
                    skipped++;
                    RecordColdCoverage(bucketId, it);
                    notes.Add($"{it.Key}：库里已有同名目录，未覆盖（可跑一次「扫描总库」认领）");
                    continue;
                }

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    Directory.Move(src, dst);
                }
                catch (Exception ex)
                {
                    failed++;
                    notes.Add($"{it.Key}：Move 入库失败 {ex.Message}");
                    TryRemoveEmptyParent(dst);
                    continue;
                }

                // 顺序是硬规则：RegisterBackup 只 UPDATE item 不建行，先 UpsertLibraryItem 才有账
                var scan = ToScanItem(it);
                _db.UpsertLibraryItem(scan);
                var add = _db.RegisterBackup(scan, _cfg.EffectiveArchiveRoot, source: "cold");

                if (!add.Added)
                {
                    // 账没落上就不能留字节：留着就是"盘上有、账上无"的孤儿，
                    // 而且 cold_item 会指向一份根本没入库的字节
                    try { Directory.Delete(dst, true); } catch { }
                    TryRemoveEmptyParent(dst);
                    failed++;
                    notes.Add($"{it.Key}：入库被拒（{add.Reason}），已回滚解出的目录");
                    continue;
                }

                // 覆盖一律以库为准：三元组用 RegisterBackup 真正写进去的那一组，
                // 不拿清单里的值再算一遍（那正是"恢复后不认已冷备"的根源）
                RecordColdCoverage(bucketId, it, add.TagNorm, add.ContentId);
                restored++;
            }

            return new ColdRestoreResult(restored, skipped, failed, false, neededBytes, notes);
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }

    /// <summary>
    /// 清理上次中断留下的解包残渣（<c>&lt;库&gt;/.favsync/restore-tmp/*</c>）。
    /// 恢复作业是单实例的，所以进去之前整目录清一遍最省事。
    /// </summary>
    public int CleanRestoreTemp()
    {
        var root = Path.Combine(_cfg.EffectiveArchiveRoot, ".favsync", "restore-tmp");
        if (!Directory.Exists(root)) return 0;
        var n = 0;
        foreach (var d in Directory.EnumerateDirectories(root))
        {
            try { Directory.Delete(d, true); n++; } catch { }
        }
        return n;
    }

    /// <summary>
    /// 恢复时把桶登记进账（不管用户有没有先点过「导入」）。
    /// 桶是<b>持久实体</b>，恢复恰恰证明它存在；缺这一行会让 <c>cold_item</c> 变成孤儿
    /// ——覆盖能判对，但面板上一个桶都看不到。
    /// </summary>
    private void EnsureBucketRegistered(ColdManifest mf, ColdManifestBucket b, string dir)
    {
        if (_db.BucketExists(b.Id)) return;
        _db.InsertBucket(new ColdBucketRow(b.Id, mf.Batch, mf.Label, SeqOf(b.Id), mf.Origin,
            b.File, b.Bytes, b.Sha256, b.Items.Count, mf.BucketMiB, StateOrPacked(b.State),
            "恢复时认领（文件在 " + dir + "）", mf.CreatedAt, mf.UpdatedAt, null, dir));
    }

    /// <summary>
    /// 记一条覆盖：这份字节在 <paramref name="bucketId"/> 里。<c>INSERT OR IGNORE</c>，绝不改归属。
    /// <para><paramref name="tagNorm"/> / <paramref name="contentId"/> 传入时以它们为准 ——
    /// 恢复路径必须传 <see cref="CatalogDatabase.RegisterBackup"/> 的返回值，
    /// 让冷备账与 backup 账用同一组键。</para>
    /// </summary>
    private void RecordColdCoverage(string bucketId, ColdManifestItem it,
        string? tagNorm = null, string? contentId = null)
    {
        var bytes = BytesOf(it);
        var sha = string.IsNullOrEmpty(it.Sha256) ? it.ContentId : it.Sha256;
        _db.ImportColdItems(bucketId, new[]
        {
            (it.Key,
             string.IsNullOrEmpty(tagNorm) ? it.TagNorm : tagNorm,
             string.IsNullOrEmpty(contentId) ? it.ContentId : contentId,
             bytes, sha),
        });
    }

    /// <summary>
    /// 桶状态只认清单里的"已上传/已删本地"，其余一律落 <c>packed</c>。
    /// 之前硬编码 <c>Packed</c> 会把 A 机已上传的桶导成"待上传 84 GiB"，纯粹吓人。
    /// </summary>
    private static string StateOrPacked(string? s) =>
        s is ColdState.Uploaded or ColdState.Pruned ? s : ColdState.Packed;

    /// <summary>Move 失败后收拾空壳父目录（只删空目录，非空一律不碰 —— 总库只增不删）。</summary>
    private static void TryRemoveEmptyParent(string dst)
    {
        try
        {
            var parent = Path.GetDirectoryName(dst);
            if (parent is not null && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                Directory.Delete(parent);
        }
        catch { }
    }

    private static string? VerifyExtracted(string src, ColdManifestItem it)
    {
        foreach (var f in it.Files)
        {
            var path = Path.Combine(src, f.Name.Replace('/', Path.DirectorySeparatorChar));
            var fi = new FileInfo(path);
            if (!fi.Exists) return $"解出来缺 {f.Name}";
            if (f.Size > 0 && fi.Length != f.Size)
                return $"{f.Name} 大小不符：{Fmt(fi.Length)} vs 清单 {Fmt(f.Size)}";
        }
        return null;
    }

    private static ScanItem ToScanItem(ColdManifestItem it)
    {
        var item = new ScanItem
        {
            Key = it.Key, Avid = it.Avid, Cid = it.Cid, Tag = it.Tag, RelDir = it.RelDir,
            Title = it.Title, Owner = it.Owner, OwnerMid = it.OwnerMid, Bvid = it.Bvid,
            Cover = it.Cover, Page = it.Page, Quality = it.Quality, Codecid = it.Codecid,
            Width = it.Width, Height = it.Height, DurationMs = it.DurationMs,
            VideoSize = it.VideoSize, VideoMd5 = it.VideoMd5,
            AudioSize = it.AudioSize, AudioMd5 = it.AudioMd5,
        };
        foreach (var f in it.Files)
            item.Files.Add(new ScanFile { Name = f.Name, Role = f.Role, Size = f.Size, Md5Declared = f.Md5 });
        return item;
    }

    // ───────────────────────── 校验 / 标记 ─────────────────────────

    /// <summary>校验一个已打好的桶：sha256 + 7z 完整性测试（逐文件 CRC 比"能解开第一个文件"强）。</summary>
    public async Task<ColdVerifyResult> VerifyBucketAsync(ColdBucketRow row, string password,
        Action<int>? onPercent, CancellationToken ct)
    {
        var exe = SevenZipExe ?? throw new InvalidOperationException(NoSevenZip);
        var path = BucketPath(row);
        if (!File.Exists(path)) return new ColdVerifyResult(false, "本地文件不在：" + path, 0, null);

        var bytes = new FileInfo(path).Length;
        var sha = SevenZip.Sha256File(path, ct);
        if (!string.IsNullOrEmpty(row.Sha256) && !string.Equals(sha, row.Sha256, StringComparison.OrdinalIgnoreCase))
            return new ColdVerifyResult(false, $"sha256 不符（文件被改过或没写完）：{sha[..12]}…", bytes, sha);

        var r = await SevenZip.TestAsync(exe, path, password, onPercent, ct).ConfigureAwait(false);
        if (!r.Ok)
            return new ColdVerifyResult(false, SevenZip.LooksLikeWrongPassword(r)
                ? "密码不对" : "完整性测试失败：" + Tail(r.Output), bytes, sha);

        _db.SetMeta("lastVerifyAt", DateTime.UtcNow.ToString("O"));
        return new ColdVerifyResult(true, $"校验通过（{Fmt(bytes)}，sha256 {sha[..12]}…）", bytes, sha);
    }

    /// <summary>桶文件的实际位置：外来桶在导入时记下的目录，本机桶在冷备份根。</summary>
    public string BucketPath(ColdBucketRow row)
    {
        var root = row.Dir ?? _cfg.EffectiveColdRoot
                   ?? throw new InvalidOperationException("还没设置冷备份目录");
        return Path.Combine(root, row.FileName);
    }

    /// <summary>
    /// 桶文件是否还在盘上（走 30 秒缓存 —— 面板每刷新一次都要问一遍，别每次去 stat 网络盘）。
    /// </summary>
    public bool BucketFileExistsCached(ColdBucketRow row)
    {
        try { return ExistsCached(BucketPath(row)); } catch { return false; }
    }

    /// <summary>删本地桶文件（云端已有一份时才允许）。账不删：<c>cold_item</c> 描述的是云端事实。</summary>
    public void PruneLocal(ColdBucketRow row)
    {
        var path = BucketPath(row);
        if (File.Exists(path)) File.Delete(path);
        _db.SetBucketState(row.BucketId, ColdState.Pruned, null);
        InvalidateProbeCache();
    }

    /// <summary>打开桶所在目录（前端"打开目录"按钮）。</summary>
    public string? BucketDir(ColdBucketRow row)
    {
        try { return Path.GetDirectoryName(BucketPath(row)); } catch { return null; }
    }
}
