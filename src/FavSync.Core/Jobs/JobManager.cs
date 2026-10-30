using System.Collections.Concurrent;
using System.Diagnostics;
using System.Formats.Tar;
using FavSync.Core.Adb;
using FavSync.Core.Bili;
using FavSync.Core.Config;
using FavSync.Core.Scan;
using FavSync.Core.Store;
using FavSync.Core.Sync;

namespace FavSync.Core.Jobs;

public sealed class JobSnapshot
{
    public required string Id { get; init; }
    public required string Kind { get; init; }        // scan | probe | sync | fav
    /// <summary>人话名（浮窗用）：扫描手机 / 探测可下载性 / 同步 / 收藏夹下载。</summary>
    public string Label { get; set; } = "";
    public string State { get; set; } = "queued";     // queued | running | done | failed | cancelled
    /// <summary>0 = 正在跑；&gt;0 = 排队位次（1 起）。</summary>
    public int QueuePos { get; set; }
    /// <summary>当前条目的动作：copy | download | upgrade（前端拼成 "同步：替代下载"）。</summary>
    public string? Phase { get; set; }
    public string Message { get; set; } = "";
    public int Total { get; set; }
    public int Done { get; set; }
    /// <summary>字节总量。0 = 未知/不适用（如扫描：总量要读完才知道）。</summary>
    public long BytesTotal { get; set; }
    /// <summary>已传输字节。BytesTotal=0 时它表示"已传输多少"而非"占比多少"。</summary>
    public long BytesDone { get; set; }
    public string? CurrentKey { get; set; }
    public string? CurrentTitle { get; set; }
    public long CurrentBytesTotal { get; set; }   // 当前条目预期字节（拷贝=手机文件集；升档下载=playurl 声明）
    public long CurrentBytesDone { get; set; }
    /// <summary>
    /// 正在处理中的条目数（下载模式 3 条并发）。<b>CurrentBytes* 只属于 CurrentKey 这一条</b>——
    /// 曾经三条并发全都往同一个 CurrentBytesDone 里累加、而 CurrentBytesTotal 只留最后一条的值，
    /// 于是出现「346 MB / 216 MB」这种明明封顶却超量的假象（用户报障）。
    /// </summary>
    public int ActiveItems { get; set; }
    /// <summary>速度单位：bytes=字节/秒；items=条/秒（探测作业按条计）。</summary>
    public string SpeedUnit { get; set; } = "bytes";
    public double SpeedBps { get; set; }
    /// <summary>本作业计划内待处理条目的 key（仅内存，不入 DTO）。items 端点用它给列表行标"排队中"。</summary>
    public HashSet<string> QueuedKeys { get; } = new(StringComparer.Ordinal);
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }
    public List<string> Log { get; } = new();

    public double Progress => Total <= 0 ? 0 : (double)Done / Total;

    public object ToDto() => new
    {
        id = Id,
        kind = Kind,
        label = Label,
        state = State,
        queuePos = QueuePos,
        phase = Phase,
        message = Message,
        total = Total,
        done = Done,
        bytesTotal = BytesTotal,
        bytesDone = BytesDone,
        speedUnit = SpeedUnit,
        currentKey = CurrentKey,
        currentTitle = CurrentTitle,
        currentBytesTotal = CurrentBytesTotal,
        currentBytesDone = CurrentBytesDone,
        activeItems = ActiveItems,
        speedBps = Math.Round(SpeedBps, 1),
        progress = Math.Round(Progress, 4),
        startedAt = StartedAt,
        endedAt = EndedAt,
        log = Log.TakeLast(30).ToArray(),
    };
}

/// <summary>
/// 作业管理器 —— <b>串行队列</b>：可以排多个作业，但同一时刻只有一个真正在跑。
/// 带宽与 adb 通道都是独占资源，并发只会让两边进度都失真，所以排队比并发更诚实。
/// 状态全在内存，进程重启即清空 —— 这是刻意的：目录与 catalog 才是唯一真相。
/// </summary>
public sealed partial class JobManager
{
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, JobSnapshot> _jobs = new();
    private readonly List<QueuedWork> _queue = new();
    private JobSnapshot? _running;
    private JobSnapshot? _latest;
    private CancellationTokenSource? _cts;
    private bool _workerActive;
    private readonly FavSyncConfig _cfg;
    private readonly AdbClient _adb;
    private readonly CatalogDatabase _db;
    private readonly BiliClient _bili;

    /// <summary>条目并发下保护 job 的计数器（Done / BytesDone / CurrentBytes* / QueuedKeys）。</summary>
    private readonly object _jobLock = new();

    private sealed record QueuedWork(JobSnapshot Job, Func<JobSnapshot, CancellationToken, Task> Work);

    /// <summary>
    /// 条目级并发度。只有下载路径值得并发：拷贝走 adb 单通道（实测聚合 0.45 MB/s，
    /// 4 路并发完全不叠加），并发只会互相抢。
    /// </summary>
    private static int SyncConcurrency(string mode) => mode == "download" ? 3 : 1;

    /// <summary>一条完成（计数 + 从待处理列表移除），必须原子——条目并发下多个线程同时进。</summary>
    private void CompleteOne(JobSnapshot job, string key)
    {
        lock (_jobLock)
        {
            job.Done++;
            job.QueuedKeys.Remove(key);
        }
        Publish(job);
    }

    /// <summary>
    /// 瞬时速率：保留最近 6 秒的（时刻, 累计字节）采样，用首尾差分。
    /// 之前用「累计字节 ÷ 作业运行秒数」，那只是全局平均——节点从 2 MB/s 跳到 20 MB/s 也看不出来。
    /// </summary>
    private sealed class SpeedMeter
    {
        private readonly List<(long Ms, long Bytes)> _q = new();

        public void Sample(long totalBytes)
        {
            var now = Environment.TickCount64;
            _q.Add((now, totalBytes));
            while (_q.Count > 2 && now - _q[0].Ms > 6000) _q.RemoveAt(0);
        }

        public double Bps
        {
            get
            {
                if (_q.Count < 2) return 0;
                var sec = (_q[^1].Ms - _q[0].Ms) / 1000.0;
                return sec <= 0.2 ? 0 : Math.Max(0, _q[^1].Bytes - _q[0].Bytes) / sec;
            }
        }
    }

    /// <summary>推的是"活动作业列表"，浮窗靠它渲染多任务。</summary>
    public event Action<IReadOnlyList<JobSnapshot>>? Updated;

    public JobManager(FavSyncConfig cfg, AdbClient adb, CatalogDatabase db, BiliClient bili)
    {
        _cfg = cfg;
        _adb = adb;
        _db = db;
        _bili = bili;
    }

    public JobSnapshot? Latest { get { lock (_gate) return _latest; } }
    public JobSnapshot? Get(string id) => _jobs.TryGetValue(id, out var j) ? j : null;

    /// <summary>有作业正在跑（排队不算忙，可继续排队）。</summary>
    public bool IsBusy { get { lock (_gate) return _running is { State: "running" }; } }

    /// <summary>运行中 + 排队中的作业（运行中的排第一）。</summary>
    public IReadOnlyList<JobSnapshot> ActiveJobs
    {
        get { lock (_gate) return BuildActiveLocked(); }
    }

    private List<JobSnapshot> BuildActiveLocked()
    {
        var list = new List<JobSnapshot>(_queue.Count + 1);
        if (_running is not null) list.Add(_running);
        foreach (var w in _queue) list.Add(w.Job);
        return list;
    }

    // 兼容点：原有几十处 Publish(job) 全部转发成"推全量活动列表"
    private void Publish(JobSnapshot _) => PublishAll();

    private void PublishAll()
    {
        IReadOnlyList<JobSnapshot> list;
        lock (_gate)
        {
            if (_running is not null) _latest = _running;
            list = BuildActiveLocked();
        }
        try { Updated?.Invoke(list); } catch { }
    }

    private string Enqueue(string kind, string label, Func<JobSnapshot, CancellationToken, Task> work)
    {
        var job = NewJob(kind, label);
        lock (_gate)
        {
            _queue.Add(new QueuedWork(job, work));
            RefreshQueuePositionsLocked();
            if (_latest is null) _latest = job;
        }
        PublishAll();
        StartWorkerIfIdle();
        return job.Id;
    }

    private void RefreshQueuePositionsLocked()
    {
        for (int i = 0; i < _queue.Count; i++) _queue[i].Job.QueuePos = i + 1;
    }

    private void StartWorkerIfIdle()
    {
        lock (_gate)
        {
            if (_workerActive) return;
            _workerActive = true;
        }
        _ = Task.Run(RunWorkerAsync);
    }

    private async Task RunWorkerAsync()
    {
        while (true)
        {
            QueuedWork next;
            lock (_gate)
            {
                if (_queue.Count == 0) { _workerActive = false; return; }
                next = _queue[0];
                _queue.RemoveAt(0);
                RefreshQueuePositionsLocked();
                _running = next.Job;
            }

            var job = next.Job;
            job.State = "running";
            job.QueuePos = 0;
            job.StartedAt = DateTime.UtcNow;

            using var cts = new CancellationTokenSource();
            lock (_gate) _cts = cts;
            PublishAll();

            try
            {
                await next.Work(job, cts.Token).ConfigureAwait(false);
                if (job.State == "running") job.State = "done";
            }
            catch (OperationCanceledException)
            {
                job.State = "cancelled";
                job.Message = "已取消";
                AddLog(job, "✗ 已取消");
            }
            catch (Exception ex)
            {
                job.State = "failed";
                job.Message = ex.Message;
                AddLog(job, "✗ " + ex.Message);
            }
            finally
            {
                job.EndedAt = DateTime.UtcNow;
                lock (_gate) { _cts = null; _running = null; }
                PublishAll();
            }
        }
    }

    public string StartScan(string? device)
    {
        var serial = ResolveSerial(device);      // 没设备要立刻报，别等排到才说
        return Enqueue("scan", "扫描手机", async (job, ct) =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                job.Message = "正在读取手机缓存清单…";
                Publish(job);

                var scanner = new Scanner(_adb);
                var scanProgress = new Progress<ScanProgress>(p =>
                {
                    if (p.Expected > 0) job.Total = p.Expected;
                    job.Done = p.Items;
                    // 字节总量流式扫描不可预知，保持 0 = 未知；BytesDone 只表示"已回传多少"
                    job.BytesDone = p.Bytes;
                    job.SpeedBps = p.Bytes / Math.Max(0.001, sw.Elapsed.TotalSeconds);
                    job.Message = $"读取元数据 {p.Items}/{Math.Max(p.Expected, p.Items)}（{p.Bytes / 1024} KB）";
                    Publish(job);   // 逐条发布：条目维度进度才有意义，别让收起条跳变
                });

                var result = await scanner.ScanAsync(serial, _cfg.PhoneRoot, p => ((IProgress<ScanProgress>)scanProgress).Report(p), ct);

                job.Total = result.Items.Count;
                job.Message = $"合并 catalog（{result.Items.Count} 条）…";
                Publish(job);

                var (added, updated, gone, drifted) = _db.MergeScan(result);

                foreach (var w in result.Warnings.Take(50)) AddLog(job, "⚠ " + w);
                if (result.Warnings.Count > 50) AddLog(job, $"⚠ 另有 {result.Warnings.Count - 50} 条 warning 未显示");
                if (drifted > 0) AddLog(job, $"⚠ {drifted} 条与总库备份不一致（手机侧已变化），已标记，未改动总库");

                job.Done = result.Items.Count;
                job.BytesDone = result.BytesReceived;   // BytesTotal 保持 0（未知）
                job.State = "done";
                job.Message = $"扫描完成：新增 {added}，更新 {updated}，离机 {gone}，漂移 {drifted}；元数据 {result.BytesReceived / 1024 / 1024} MB / {sw.Elapsed.TotalSeconds:F0}s";
                job.EndedAt = DateTime.UtcNow;
                AddLog(job, job.Message);
            }
            catch (Exception ex)
            {
                job.State = "failed";
                job.Message = ex.Message;
                job.EndedAt = DateTime.UtcNow;
                AddLog(job, "✗ " + ex.Message);
            }
            finally
            {
                Publish(job);
            }
        });
    }

    /// <summary>
    /// 扫描**总库**（不是手机）：把磁盘上已经存在的目录认领入库。
    ///
    /// <para><b>为什么不能复用 <see cref="StartScan"/>：</b>扫手机走 <c>MergeScan</c>，会覆盖
    /// <c>phone_*</c>、并把本次没扫到的条目置 <c>on_phone=0</c>。扫库时手机不在场，
    /// 写 <c>phone_*</c> 就是造假；把 4000 多条已同步条目打成"离机"会直接毁掉同步页口径。
    /// 这里唯一允许的落库方法是 <see cref="CatalogDatabase.RegisterBackup"/>——只写 backup 账目、
    /// 翻 <c>state='synced'</c>，并按 <c>(key,tag_norm,content_id)</c> 判重，天然幂等可重跑。</para>
    ///
    /// <para>用途：中继回传后认领、手工搬条目进库、换机迁移、库体检。
    /// 中继场景下「扫完仍是 <c>phone_only</c> 的条目」就是没传成功的，不需要另外出报告。</para>
    /// </summary>
    public string StartLibraryScan()
    {
        return Enqueue("library-scan", "扫描总库", async (job, ct) =>
        {
            var sw = Stopwatch.StartNew();
            var root = _cfg.EffectiveArchiveRoot;
            try
            {
                job.Phase = "walk";
                job.Message = $"正在遍历总库 {root}…";
                Publish(job);

                var scanner = new Scanner(_adb);
                var source = new LocalLibrarySource(root);
                var result = await scanner.ScanSourceAsync(source, p =>
                {
                    if (p.Expected > 0) job.Total = p.Expected;
                    job.Done = p.Items;
                    job.BytesDone = p.Bytes;
                    job.SpeedBps = p.Bytes / Math.Max(0.001, sw.Elapsed.TotalSeconds);
                    job.Message = $"读取元数据 {p.Items}/{Math.Max(p.Expected, p.Items)}（{p.Bytes / 1024} KB）";
                    Publish(job);
                }, ct);

                job.Phase = "claim";
                job.Total = result.Items.Count;
                job.Done = 0;
                job.BytesDone = 0;
                job.Message = $"认领入库（{result.Items.Count} 条）…";
                Publish(job);

                var knownIds = _db.ExistingBackupIds();
                var knownKeys = _db.ExistingItemKeys();

                int claimed = 0, duplicate = 0, created = 0, failed = 0;

                foreach (var item in result.Items)
                {
                    if (ct.IsCancellationRequested) { job.State = "cancelled"; break; }

                    var id = CatalogDatabase.BackupId(item.Key, item.TagNorm, item.ContentId);
                    if (knownIds.Contains(id)) { duplicate++; job.Done++; continue; }

                    try
                    {
                        // 磁盘上有、item 表没有 → 先建条目行（ON CONFLICT DO NOTHING，不会动已有行）
                        if (!knownKeys.Contains(item.Key))
                        {
                            if (_db.UpsertLibraryItem(item)) { created++; knownKeys.Add(item.Key); }
                        }

                        var add = _db.RegisterBackup(item, root, source: "library");
                        if (add.Added) { claimed++; knownIds.Add(id); }
                        else duplicate++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        AddLog(job, $"✗ {item.Key}：{ex.Message}");
                    }

                    job.Done++;
                    if (job.Done % 50 == 0)
                    {
                        job.Message = $"认领入库 {job.Done}/{result.Items.Count}（新认领 {claimed}）";
                        Publish(job);
                    }
                }

                // ---- 账实核对（fsck）----
                // 只报告，不写库。扫不到的条目（典型是早年「收藏夹直下」，没有 index.json，
                // dash 信息只存在 db 里）改用账目里的字节数去核对，而不是靠 index.json。
                job.Phase = "verify";
                job.Message = "账实核对…";
                Publish(job);

                var scannedKeys = new HashSet<string>(result.Items.Select(i => i.Key), StringComparer.Ordinal);
                var primaries = _db.LibraryPrimaries();
                var damaged = new List<string>();
                int verified = 0;

                foreach (var p in primaries)
                {
                    if (scannedKeys.Contains(p.Key)) { verified++; continue; }   // 已由 index.json 认领/命中

                    var dir = Path.Combine(root, p.RelDir.Replace('/', Path.DirectorySeparatorChar), p.TagRaw);
                    var vpath = Path.Combine(dir, "video.m4s");
                    var apath = Path.Combine(dir, "audio.m4s");

                    if (!File.Exists(vpath) || !File.Exists(apath))
                    {
                        damaged.Add($"{p.Key}：{p.RelDir}/{p.TagRaw} 缺 {(File.Exists(vpath) ? "audio" : "video")}.m4s");
                        continue;
                    }

                    var sv = new FileInfo(vpath).Length;
                    var sa = new FileInfo(apath).Length;
                    if (p.VideoSize > 0 && sv != p.VideoSize)
                    {
                        damaged.Add($"{p.Key}：video 实际 {sv} 字节，账目 {p.VideoSize}（差 {p.VideoSize - sv}）");
                        continue;
                    }
                    if (p.AudioSize > 0 && sa != p.AudioSize)
                    {
                        damaged.Add($"{p.Key}：audio 实际 {sa} 字节，账目 {p.AudioSize}（差 {p.AudioSize - sa}）");
                        continue;
                    }
                    verified++;
                }

                // 磁盘上有条目目录、既没有 index.json 也没有账目：认不出来，只能报出来
                var accountedKeys = new HashSet<string>(primaries.Select(p => p.Key), StringComparer.Ordinal);
                var unrecognized = result.NoIndexDirs
                    .Where(rel => !accountedKeys.Contains(Scanner.MakeKey(rel)))
                    .ToList();

                foreach (var w in result.Warnings.Take(50)) AddLog(job, "⚠ " + w);
                if (result.Warnings.Count > 50) AddLog(job, $"⚠ 另有 {result.Warnings.Count - 50} 条 warning 未显示");

                job.State = "done";
                job.Message = $"扫库完成：识别 {result.Items.Count} 条（新认领 {claimed}、已入账 {duplicate}）"
                            + $"；账实核对 {primaries.Count} 条（完好 {verified}、异常 {damaged.Count}）"
                            + (unrecognized.Count > 0 ? $"；无法识别 {unrecognized.Count}" : "")
                            + $"；{sw.Elapsed.TotalSeconds:F0}s";
                job.EndedAt = DateTime.UtcNow;
                AddLog(job, job.Message);

                if (claimed > 0)
                    AddLog(job, $"✓ {claimed} 条已从「未同步」翻为「已同步」");
                if (created > 0)
                    AddLog(job, $"ℹ 补建 {created} 条条目行（磁盘上有文件、账目里根本没有）");
                if (failed > 0)
                    AddLog(job, $"✗ {failed} 条认领时报错，见上方明细");
                if (result.NoIndexDirs.Count > 0)
                    AddLog(job, $"ℹ {result.NoIndexDirs.Count} 条条目没有 index.json（多为收藏夹直下），已改用账目核对字节");
                foreach (var d in damaged.Take(20)) AddLog(job, "✗ " + d);
                if (damaged.Count > 20) AddLog(job, $"✗ 另有 {damaged.Count - 20} 条异常未显示");
                if (damaged.Count > 0)
                    AddLog(job, "⚠ 上面这些是「账目说有、磁盘上没有或不符」——同步页不会自动降级它们，需人工处理");
                foreach (var u in unrecognized.Take(10)) AddLog(job, "⚠ 无法识别：" + u);
            }
            catch (Exception ex)
            {
                job.State = "failed";
                job.Message = ex.Message;
                job.EndedAt = DateTime.UtcNow;
                AddLog(job, "✗ " + ex.Message);
            }
            finally
            {
                Publish(job);
            }
        });
    }

    /// <summary>
    /// 可下载性探测作业（S4）。必须登录：未登录时 playurl 高档位下发不全，
    /// 120/116 等档会被误判成 lower —— 那会让"不可替代"误报泛滥。
    ///
    /// 节流由 <see cref="BiliClient.Pacer"/> 统一管（AIMD 间隔 + 撞墙熔断）：
    /// 每条只需一次 playurl（不打 view），干净路径实测 0.2s 网络 + 0.32~0.4s 间隔 ≈ 0.6s/条；
    /// 撞风控则冷却 60/180/300s 后用同一条当探针，而不是继续以 11 条/分钟猛打同一堵墙。
    /// </summary>
    public string StartProbe(IReadOnlyList<string>? onlyKeys = null)
    {
        if (!_bili.HasCookies)
            throw new InvalidOperationException("请先扫码登录 B 站：未登录时高档位探测不全，结果不可信");

        return Enqueue("probe", "探测可下载性", async (job, ct) =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var account = await _bili.WhoAmIAsync(ct).ConfigureAwait(false);
                if (!account.LoggedIn)
                    throw new InvalidOperationException("B 站登录态已失效，请重新扫码登录");
                AddLog(job, $"登录态：{account.Uname ?? account.Mid.ToString()}（{account.Mid}）");

                var candidates = _db.ProbeCandidates(onlyKeys);
                if (onlyKeys is { Count: > 0 })
                    AddLog(job, $"指定重探 {candidates.Count} 条（忽略缓存有效期）");
                job.Total = candidates.Count;
                job.Message = $"待探测 {candidates.Count} 条";
                Publish(job);
                if (candidates.Count == 0)
                {
                    job.State = "done";
                    job.Message = "没有需要探测的条目（缓存都在有效期内）";
                    job.EndedAt = DateTime.UtcNow;
                    AddLog(job, job.Message);
                    Publish(job);
                    return;
                }

                int exact = 0, lower = 0, gone = 0, unknown = 0, charge = 0;
                var pacer = _bili.Pacer;
                var meter = new SpeedMeter();              // 6 秒滑窗测"条/秒"，不用累计平均
                var lastPublish = Environment.TickCount64;

                void PublishProgress(string msg)
                {
                    job.Message = msg;
                    job.SpeedUnit = "items";
                    job.SpeedBps = meter.Bps;
                    Publish(job);
                    lastPublish = Environment.TickCount64;
                }

                var aborted = false;
                foreach (var c in candidates)
                {
                    ct.ThrowIfCancellationRequested();

                    // 熔断冷却 + 基础间隔都在 pacer 里等，作业只负责把"在等什么"显示出来
                    await pacer.WaitTurnAsync(ct, left =>
                    {
                        PublishProgress($"⏸ 撞 B 站风控，冷却中 剩 {left}s（第 {pacer.CooldownCount} 次，间隔已放到 {pacer.DelayMs}ms）· 已探 {job.Done}/{job.Total}");
                    }).ConfigureAwait(false);

                    ProbeOutcome outcome;
                    var giveUp = false;
                    for (int tryNo = 1; ; tryNo++)
                    {
                        outcome = await _bili.ProbeAsync(c.Avid, c.Cid, c.Quality, c.Codecid, c.VideoSize, ct).ConfigureAwait(false);
                        if (!outcome.Throttled) break;

                        if (tryNo >= 2)
                        {
                            // 冷却一整轮（≥60s）回来还是墙 —— 说明窗口比一轮长得多，这时继续"每条烧 3 轮冷却"
                            // 是最坏的选择（单条能烧掉十几分钟）。整单收工，剩余条目留着下轮再探，
                            // 并且给下一轮也预约一次更长的冷却，别让用户点一下"开始探测"就又从 1.6 QPS 冲墙。
                            pacer.ReportThrottled();
                            giveUp = aborted = true;
                            break;
                        }

                        // 撞墙：不写库（写了就是把"环境坏了"记成"这条探不出"），冷却后拿这一条当探针
                        var sec = pacer.ReportThrottled();
                        AddLog(job, $"⚠ {c.Key}：风控降级，冷却 {sec}s 后用它当探针重探（第 {tryNo} 次）");
                        await pacer.WaitTurnAsync(ct, left =>
                        {
                            PublishProgress($"⏸ 撞风控，冷却中 剩 {left}s（已探 {job.Done}/{job.Total}，间隔放到 {pacer.DelayMs}ms）");
                        }).ConfigureAwait(false);
                    }
                    if (giveUp) break;

                    _db.SaveProbe(c.Key, outcome.Verdict, outcome.Note, outcome.MaxQuality, outcome.MaxCodecid);
                    pacer.ReportClean();
                    meter.Sample(job.Done);

                    switch (outcome.Verdict)
                    {
                        case ProbeVerdict.Exact: exact++; break;
                        case ProbeVerdict.Lower: lower++; break;
                        case ProbeVerdict.Gone: gone++; break;
                        case ProbeVerdict.Charge: charge++; lower++; break;   // 充电 → 归入"仅拷贝"
                        default: unknown++; break;
                    }
                    job.Done++;

                    // 每 5 条或每 1.2 秒刷一次：撞风控降速时 25 条一刷 = 两分钟不动，看着像卡死
                    if (job.Done % 5 == 0 || job.Done == job.Total ||
                        Environment.TickCount64 - lastPublish >= 1200)
                    {
                        PublishProgress($"探测 {job.Done}/{job.Total} · 失效 {gone} · 仅拷贝 {lower} · 可替代 {exact} · 未知 {unknown}" +
                            (charge > 0 ? $" · 充电 {charge}" : ""));
                    }
                    if (outcome.Verdict is ProbeVerdict.Gone && outcome.Note is not null && (gone <= 20 || job.Done == job.Total))
                        AddLog(job, $"✗ {c.Key}：{outcome.Note}（不可下载替代，只能手机拷贝）");
                    else if (outcome.Verdict is ProbeVerdict.Charge && outcome.Note is not null && (charge <= 20 || job.Done == job.Total))
                        AddLog(job, $"⚡ {c.Key}：{outcome.Note}（充电视频，只能手机拷贝）");
                }

                var chargeTail = charge > 0 ? $" · 充电 {charge}" : "";
                job.State = "done";
                job.Message = aborted
                    ? $"风控未解除，提前收工：已探 {job.Done}/{job.Total} 条 · 失效 {gone} · 仅拷贝 {lower} · 可替代 {exact} · 未知 {unknown}{chargeTail}，" +
                      $"剩 {job.Total - job.Done} 条留着下轮（探测间隔已放到 {pacer.DelayMs}ms），{sw.Elapsed.TotalSeconds:F0}s"
                    : $"探测完成：{job.Total} 条 · 失效 {gone} · 仅拷贝 {lower} · 可替代 {exact} · 未知 {unknown}{chargeTail}，{sw.Elapsed.TotalSeconds:F0}s";
                job.EndedAt = DateTime.UtcNow;
                AddLog(job, job.Message);
                Publish(job);
            }
            catch (OperationCanceledException)
            {
                job.State = "cancelled";
                job.Message = "探测已取消";
                job.EndedAt = DateTime.UtcNow;
                AddLog(job, job.Message);
                Publish(job);
            }
            catch (Exception ex)
            {
                job.State = "failed";
                job.Message = ex.Message;
                job.EndedAt = DateTime.UtcNow;
                AddLog(job, "✗ " + ex.Message);
                Publish(job);
            }
        });
    }

    /// <summary>
    /// mode：auto（默认，probe=exact 走下载、失败回退拷贝）| copy（只走手机）| download（只走下载：
    /// exact 字节级替代；lower/unknown 现场决策——有 ≥手机档位 就升档下载，只有更低档则跳过留给拷贝）。
    /// keys 为空且 mode=download 时自动选取全部可下载候选（exact/lower/unknown，体积降序）。
    /// 探测作业与同步共用单作业槽，天然与 playurl API 互斥（实测叠加流量触发 v_voucher 风控）。
    /// </summary>
    public string StartSync(IReadOnlyList<string> keys, string? device, string mode = "auto")
    {
        var autoPicked = false;
        if (keys.Count == 0 && mode == "download")
        {
            keys = _db.DownloadCandidateKeys();
            autoPicked = true;
            if (keys.Count == 0)
                throw new InvalidOperationException("没有可下载候选（全部已同步，或没有 exact/lower/unknown 探测记录）");
        }
        if (keys.Count == 0) throw new InvalidOperationException("没有选中任何条目");

        // label 固定为「同步」：具体走哪条路由 phase 体现（同步：替代下载 / 升级下载 / 手机拷贝）
        var label = "同步";
        var notes = new List<string>();
        if (autoPicked) notes.Add($"未指定条目，自动选取 {keys.Count} 条可下载候选（exact/lower/unknown）");
        var serial = ResolveSerial(device);
        var queuedKeys = new List<string>();
        var plan = new List<(string Key, ItemRow Row)>();
        foreach (var k in keys)
        {
            var row = _db.Get(k);
            if (row is null) { notes.Add($"跳过 {k}：catalog 中不存在"); continue; }
            if (row.State == ItemState.Incomplete) { notes.Add($"跳过 {k}：手机侧残片，禁止同步"); continue; }

            if (row.State == ItemState.Synced)
            {
                // webmux（内容一致、库里那份是网页版封装）扫描时已判 synced，但元数据还能补成
                // 手机原版（entry.json / 弹幕）—— 放它进 plan 走"只补元数据"分支，不搬 m4s。
                if (!Drift.Has(row.Drift, Drift.WebMux))
                {
                    // 其余 synced 不产生新备份条目：内容没变就没有新东西可存
                    var why = Drift.Has(row.Drift, Drift.TagDown) ? "库里那份更好，这份不重复入库"
                            : Drift.Has(row.Drift, Drift.Structure) ? "内容相同，仅文件集变化，不重复入库"
                            : "已同步";
                    notes.Add($"跳过 {k}：{why}");
                    continue;
                }
            }

            if (row.State == ItemState.Outdated)
                notes.Add($"替换 {k}：{row.DriftNote ?? row.Drift}（旧的那份会进回收站）");

            plan.Add((k, row));
            queuedKeys.Add(k);
        }

        var need = plan.Sum(p => p.Row.PhoneSize);
        var (spaceOk, needBytes, freeBytes) = Preflight.CheckSpace(_cfg, need);
        if (!spaceOk)
            throw new InvalidOperationException(
                $"总库空间不足：需要 {needBytes / 1024 / 1024} MB，剩余 {freeBytes / 1024 / 1024} MB");

        return Enqueue("sync", label, async (job, ct) =>
        {
            foreach (var n in notes) AddLog(job, n);
            foreach (var k in queuedKeys) job.QueuedKeys.Add(k);
            var sw = Stopwatch.StartNew();
            var speed = new SpeedMeter();   // 作业级，跨条目连续（别每条重建，否则开头几秒速率是错的）
            var copier = new ItemCopier(_adb);
            job.Total = plan.Count;
            job.BytesTotal = need;
            Publish(job);

            var concurrency = SyncConcurrency(mode);
            if (concurrency > 1)
                AddLog(job, "条目并发 " + concurrency + "（让 adb 元数据 / playurl / md5 / 入库这些固定开销与别的条目的下载重叠）");
            using var gate = new SemaphoreSlim(concurrency, concurrency);
            await Task.WhenAll(plan.Select(async item =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                lock (_jobLock) job.ActiveItems++;
                try { await ProcessOneAsync(item.Key, item.Row).ConfigureAwait(false); }
                finally
                {
                    lock (_jobLock) job.ActiveItems--;
                    gate.Release();
                }
            })).ConfigureAwait(false);

            async Task ProcessOneAsync(string key, ItemRow row)
            {
                if (ct.IsCancellationRequested) { job.State = "cancelled"; return; }

                lock (_jobLock)
                {
                    job.CurrentKey = key;
                    job.CurrentTitle = row.Title ?? key;
                }
                var probe = _db.GetProbe(key);
                // 下载准入（用户拍板）：只下"能拿回同一份字节"或"能拿回更高画质"的。
                // 同画质不同编码（手机 HEVC / B站 AVC·AV1）一律不下 —— 手机那份才是原件，
                // 拿异编码副本入库会把"原件还在手机"这个事实盖掉，而且会永远重下（tag 永远对不上）。
                var dl = DownloadGate(row, DateTime.UtcNow);
                var useDownload = mode == "download"
                    ? dl.Allow
                    : mode == "auto" && probe?.Verdict == ProbeVerdict.Exact;
                lock (_jobLock) { if (job.CurrentKey == key) job.Phase = useDownload ? "download" : "copy"; }
                if (mode == "download" && !dl.Allow)
                {
                    AddLog(job, $"跳过 {key}：{dl.Reason}");
                    CompleteOne(job, key); return;
                }

                var info = _db.GetPhoneFiles(key);
                if (info.Count == 0) { AddLog(job, $"✗ {key}：文件集缺失"); CompleteOne(job, key); return; }

                // 主副本固定落在镜像路径 <avid>/c_<cid>/，**第一次入库后路径永不变动**；
                // 之后任何新增条目（换源、升档）都进 .versions/ —— 那里以 '.' 开头，
                // 外部播放器的 dual 扫描器会跳过，所以不会和主副本撞 key。
                // staging 按 key 稳定（不带 runId）：同一 key 重跑时断点续传 manifest 才能续上；
                // 单作业槽保证同一 staging 不会被两个作业同时写。Commit 是整目录 Move，提交后自然清空。
                // 落点永远是镜像路径 <avid>/c_<cid>/。.versions 不再是长期仓库：
                // 替换时旧主先搬进回收站腾空这里（见下方"替换"分支）。
                // staging 也沿用镜像路径 —— 与历史断点续传目录一致，不必重新算。
                var relTarget = row.RelDir;
                var staging = Path.Combine(_cfg.IncomingRoot, relTarget.Replace('/', Path.DirectorySeparatorChar));
                var target = Path.Combine(_cfg.EffectiveArchiveRoot, relTarget.Replace('/', Path.DirectorySeparatorChar));

                var expected = info.ToList();

                // 当前条目进度：总预期先按手机文件集算（拷贝路径精确；同档下载字节一致），
                // 升档下载时 DownloadViaDash 会用 playurl 声明大小覆盖。
                // **并发铁律**：meter 是这条自己的账，job.CurrentBytes* 只在自己仍是 CurrentKey 时写。
                // 三条并发共用一个 CurrentBytesDone、而 CurrentBytesTotal 只留最后一条的值，
                // 就会算出「346 MB / 216 MB · 100%」这种封顶了还在涨的假数字（用户报障）。
                var meter = new ItemMeter(expected.Sum(f => f.Size));
                lock (_jobLock)
                {
                    if (job.CurrentKey == key)
                    {
                        job.CurrentBytesTotal = meter.Total;
                        job.CurrentBytesDone = 0;
                    }
                }
                long lastPublishMs = 0;
                var bytesProgress = new Progress<long>(delta =>
                {
                    double bps;
                    bool report;
                    long nowMs;
                    lock (_jobLock)   // 条目并发：多个下载线程同时改计数
                    {
                        job.BytesDone += delta;
                        meter.Done += delta;
                        // 分母是"问出来的流长"，万一是估计值（音频换档、CDN 少报），
                        // 就以已下字节为准往上抬 —— 宁可分母跳动，也不许出现「已下 > 应有」
                        if (meter.Total > 0 && meter.Done > meter.Total) meter.Total = meter.Done;
                        speed.Sample(job.BytesDone);
                        bps = speed.Bps;
                        if (job.CurrentKey == key) job.CurrentBytesDone = meter.Done;
                        // 节流按时间：以前拿「比我上次报的多」当条件，并发下别条也在加字节，
                        // 等于每次 4 MB 回调都 Publish（一秒几十次 JSON 序列化 + 前端重绘）
                        nowMs = Environment.TickCount64;
                        report = nowMs - lastPublishMs >= 200;
                        if (report) { lastPublishMs = nowMs; job.SpeedBps = bps; }
                    }
                    if (report) Publish(job);
                });

                // 分母换成真实流长（0 = 问不到，UI 只显示已下字节，不硬编一个分母）
                void SetTotal(long t)
                {
                    lock (_jobLock)
                    {
                        meter.Total = t;
                        if (t > 0) meter.Done = Math.Min(meter.Done, t);
                        if (job.CurrentKey == key)
                        {
                            job.CurrentBytesTotal = t;
                            job.CurrentBytesDone = meter.Done;
                        }
                    }
                }

                // 条目收尾：若它还是「当前条目」，把进度拉到 100% —— 否则并发的另一条抢先
                // 写 CurrentKey 后，这条会永远停在 92%，看着像没下完
                void FinishMeter()
                {
                    lock (_jobLock)
                    {
                        meter.Total = Math.Max(meter.Total, meter.Done);
                        if (job.CurrentKey == key)
                        {
                            job.CurrentBytesTotal = meter.Total;
                            job.CurrentBytesDone = meter.Total;
                        }
                    }
                }

                // ---- 下载路径（实测：Web dash 与手机缓存字节级一致，md5 可严格比对）----
                if (useDownload)
                {
                    job.Message = $"下载 {row.Title ?? key}";
                    Publish(job);
                    Outcome? dlOutcome = null;
                    try
                    {
                        dlOutcome = await DownloadViaDash(
                            job, row, staging, serial, _cfg.PhoneRoot, expected, bytesProgress, SetTotal, ct)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        AddLog(job, $"△ {key}：下载失败（{ex.Message}）" +
                            (mode == "download" ? "" : "，回退手机拷贝"));

                        // 下载现场撞上 87008 = 充电视频。用户口径：不做预先探测，
                        // 失败了才打标 —— 这里就把结论写回 probe，下一轮 DownloadGate 直接跳过，
                        // 不必再白跑一次 playurl + 建 staging。
                        if (ex.Message.Contains("87008"))
                            _db.SaveProbe(key, ProbeVerdict.Charge, "充电视频：未充电或已过期，B站不给流");
                    }

                    if (dlOutcome is { } ok && ok.Ok)
                    {
                        var dlCommit = ItemCopier.Commit(staging, target);
                        if (dlCommit.Ok)
                        {
                            var si = ToScanItem(row, expected);
                            if (ok.Upgraded)
                            {
                                // 升档下载：入库账目记**实际落盘**的档位/大小/md5（≠手机账目，属预期——
                                // 总库存的是比手机更好的字节，低档不入库的既有规则天然放行）
                                si.Quality = ok.ActualQuality;
                                si.Codecid = ok.ActualCodecid;
                                si.VideoMd5 = ok.VideoMd5;
                                si.AudioMd5 = ok.AudioMd5;
                                si.VideoSize = ok.VideoBytes;
                                si.AudioSize = ok.AudioBytes;
                            }
                            var dlReg = _db.RegisterBackup(si, _cfg.EffectiveArchiveRoot,
                                source: "backup", via: "download");
                            AddLog(job, dlReg.Added
                                ? $"✓ {key} 下载替代 {ok.Bytes / 1024 / 1024} MB（{(ok.Upgraded ? $"升档 q{ok.ActualQuality}，声明大小校验" : "md5 全符")}，{dlReg.Reason}，{dlReg.Path}）"
                                : $"= {key} 下载字节已到位但未新增条目：{dlReg.Reason}");
                            FinishMeter();
                            CompleteOne(job, key);
                            return;
                        }
                        AddLog(job, $"✗ {key}：{dlCommit.Error}");
                        CompleteOne(job, key); return;
                    }

                    if (mode == "download")
                    {
                        // 取消/中断：播放中途停下的失败不是"字节不对"，别标 mismatch。
                        // **保留 staging** —— 下载有断点 manifest，下轮同 key 可续传
                        if (ct.IsCancellationRequested)
                        {
                            AddLog(job, $"⏹ {key}：下载被中断，未入库；staging 已保留待续传，可重新同步");
                            CompleteOne(job, key); return;
                        }
                        // 强制下载模式：不回退拷贝。md5 闸门拒收 = download_unreliable → 清现场标 mismatch；
                        // 其他失败（风控降级/网络）**保留 staging**，下轮同 key 可断点续传
                        var err = dlOutcome?.Error ?? "下载异常";
                        if (err.Contains("download_unreliable"))
                        {
                            ItemCopier.TryDeleteDirectory(staging);
                            // 原因落库：网页下载的字节与 playurl / md5 声明不符，闸门拒收
                            _db.SetState(key, ItemState.Mismatch, "下载字节校验未通过：" + Clip(err, 300));
                            AddLog(job, $"✗ {key}：download_unreliable（{err}），未入库任何字节");
                        }
                        else
                        {
                            AddLog(job, $"跳过 {key}：下载未成（{err}），staging 已保留待续传");
                        }
                        CompleteOne(job, key); return;
                    }
                    // auto：清掉 staging 里的半成品，落回手机拷贝
                    ItemCopier.TryDeleteDirectory(staging);
                }

                // ---- 元数据补齐（drift=webmux）：库里已有同画质同编码的网页版字节 ----
                // 手机这份是原版缓存，内容一致、只是封装不同（web mux ≠ 手机 mux）→
                // 不重搬 m4s（省掉上百 MB 传输），但把手机正版的元数据覆盖进主副本：
                // 我们自己合成的那份 entry.json 缺 title / owner_id / 宽高，
                // 外部播放器扫到会退化（标题显示成 key、UP 主合集全部塌进「未识别 UP 主」）。
                // index.json 不拷 —— 它声明的是手机字节的 md5/size，拷进装网页版 m4s 的目录
                // 会让外部播放器判「大小不符 → 损坏」，好心办坏事。
                if (Drift.Has(row.Drift, Drift.WebMux) && row.BackupCount > 0)
                {
                    var primaryDir = Path.Combine(_cfg.EffectiveArchiveRoot,
                        row.RelDir.Replace('/', Path.DirectorySeparatorChar));
                    var metaExpected = expected.Where(IsTopUpFile).ToList();

                    if (metaExpected.Count == 0 || ItemCopier.IsPhoneGradeEntry(primaryDir))
                    {
                        AddLog(job, $"= {key} 元数据已是手机原版，无需补齐（字节沿用收藏夹直下的网页版）");
                        _db.SetState(key, ItemState.Synced);
                        FinishMeter();
                        CompleteOne(job, key);
                        return;
                    }

                    job.Message = $"补齐元数据 {row.Title ?? key}";
                    Publish(job);
                    SetTotal(metaExpected.Sum(f => f.Size));

                    var topUp = await copier.CopyAsync(serial, _cfg.PhoneRoot, row.RelDir,
                        staging, metaExpected, null, null, bytesProgress, ct, metaOnly: true);
                    if (!topUp.Ok)
                    {
                        // 取消 ≠ 校验失败（见下方手机拷贝分支的说明），保持原状态
                        if (ct.IsCancellationRequested)
                        {
                            AddLog(job, $"⏹ {key}：元数据补齐被中断，状态保持原样，可重新同步");
                            CompleteOne(job, key);
                            return;
                        }
                        _db.SetState(key, ItemState.Mismatch, "元数据补齐未通过：" + Clip(topUp.Error, 300));
                        AddLog(job, $"✗ {key}：{topUp.Error}");
                        CompleteOne(job, key);
                        return;
                    }

                    var tc = ItemCopier.CommitTopUp(staging, primaryDir, metaExpected.Select(f => f.Name));
                    if (!tc.Ok)
                    {
                        AddLog(job, $"✗ {key}：{tc.Error}");
                        CompleteOne(job, key);
                        return;
                    }

                    // 正版 entry.json 里带着 UP 主 mid：零额外请求，顺手落库
                    var mid = TryReadOwnerMid(Path.Combine(primaryDir, "entry.json"));
                    if (mid is { } m) _db.SetOwnerMid(key, m);
                    _db.SetState(key, ItemState.Synced);
                    AddLog(job, $"✓ {key} 补齐 {tc.FileCount} 个元数据文件（{string.Join(", ", metaExpected.Select(f => f.Name))}）" +
                                (mid is { } m2 ? $"，UP mid={m2}" : "") + "，字节沿用网页版");
                    FinishMeter();
                    CompleteOne(job, key);
                    return;
                }

                // ---- 手机拷贝路径 ----
                // staging 已改为按 key 稳定：上一轮下载可能留下断点 manifest，拷贝路径全覆盖写入
                // 每个期望文件，但 manifest 是下载路径的私货，进总库前必须清掉
                if (Directory.Exists(staging))
                {
                    foreach (var m in Directory.EnumerateFiles(staging, "*.resume.json", SearchOption.AllDirectories))
                    {
                        try { File.Delete(m); } catch { }
                    }
                }
                job.Message = $"搬运 {row.Title ?? key}";
                Publish(job);
                var outcome = await copier.CopyAsync(
                    serial, _cfg.PhoneRoot, row.RelDir,
                    staging, expected, row.PhoneVideoMd5, row.PhoneAudioMd5,
                    bytesProgress, ct);

                if (!outcome.Ok)
                {
                    // 取消/中断 ≠ 校验失败。标成 mismatch 会让这条卡在「校验失败」红色终态里
                    // （前端也把它当不可再选的死条目），用户看到的就是"拷到一半取消后再也选不中"。
                    // 这里保持原状态（phone_only / outdated），下次同步自然重新带上它。
                    if (ct.IsCancellationRequested)
                    {
                        ItemCopier.TryDeleteDirectory(staging);
                        AddLog(job, $"⏹ {key}：搬运被中断，未入库；状态保持「{(row.State == ItemState.Outdated ? "有变化" : "待同步")}」，可直接重新同步");
                        CompleteOne(job, key);
                        return;
                    }
                    // 搬运闸门（文件集/大小/md5）没过 → 原因落库，列表里能直接看到"校验哪一步失败"
                    _db.SetState(key, ItemState.Mismatch, "搬运校验未通过：" + Clip(outcome.Error, 300));
                    AddLog(job, $"✗ {key}：{outcome.Error}");
                    CompleteOne(job, key);
                    return;
                }

                // ---- 替换（用户拍板）----
                // 库里已有这份、手机这份更好（含同档更优编码）或内容不同 → 手机这份直接当主副本，
                // 旧主整棵移进回收站（能看、能清空）。
                // 旧规则（主副本永不变动、新份进 .versions/）取消了：实测 .versions 的字节
                // 不被扫库看见、不参与冷备、不进预览，只是纯占空间（生产库建了一直只有 1 条 352 KB）。
                // 顺序铁律不变：先搬磁盘、成功才动账；任一步失败必须整体还原。
                var replacing = row.BackupCount > 0;
                BackupRow? oldPrimary = null;
                string? trashRoot = null;
                var commitPhase = "committed";
                var parkedRel = row.RelDir.Replace('/', Path.DirectorySeparatorChar);

                if (replacing)
                {
                    oldPrimary = _db.GetPrimary(key);
                    if (oldPrimary is null)
                    {
                        replacing = false;   // 账实不符的极端情况：按首次入库走，RegisterBackup 会补行
                        AddLog(job, $"△ {key}：库里标着有备份，但找不到主副本账目 —— 按首次入库处理");
                    }
                    else if (!string.Equals(oldPrimary.Path.Replace('\\', '/'), row.RelDir, StringComparison.OrdinalIgnoreCase))
                    {
                        // 主副本账目不在镜像位置（.versions 时代的历史数据）：不猜，停下来交给人处理
                        AddLog(job, $"✗ {key}：主副本账目路径不在镜像位置（{oldPrimary.Path}），未替换；先清掉历史附加版本再同步");
                        CompleteOne(job, key);
                        return;
                    }
                    else
                    {
                        // 暂存放在 .trash/<key>-swap-<ts>/：同卷 Move 是瞬时的，失败能原地搬回；
                        // 账目落成后再补 meta.json，就变成一条正常的回收站记录。
                        trashRoot = Path.Combine(_cfg.EffectiveArchiveRoot, ".trash",
                            TrashName(key) + "-swap-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
                        if (!TryMoveDir(target, Path.Combine(trashRoot, parkedRel), out var mvErr))
                        {
                            AddLog(job, $"✗ {key}：腾空主目录失败（{mvErr}），未替换");
                            CompleteOne(job, key);
                            return;
                        }
                    }
                }

                var commit = ItemCopier.Commit(staging, target);
                if (!commit.Ok)
                {
                    if (trashRoot is not null)
                        TryMoveDir(Path.Combine(trashRoot, parkedRel), target, out _);   // 旧副本还原到原处
                    AddLog(job, $"✗ {key}：{commit.Error}" + (replacing ? "（旧副本已还原）" : ""));
                    CompleteOne(job, key);
                    return;
                }
                commitPhase = replacing ? "替换" : commit.Phase;

                var scanItem = ToScanItem(row, expected);
                var reg = _db.RegisterBackup(scanItem, _cfg.EffectiveArchiveRoot,
                    source: "backup", via: "copy", replaceExistingPrimary: replacing);

                if (!reg.Added)
                {
                    // 账目没落成 → 新主删掉、旧主搬回，绝不留"字节还在、账上没有"的撕裂态
                    ItemCopier.TryDeleteDirectory(target);
                    if (trashRoot is not null)
                        TryMoveDir(Path.Combine(trashRoot, parkedRel), target, out _);
                    AddLog(job, $"✗ {key}：账目未落成（{reg.Reason}），已还原");
                    FinishMeter();
                    CompleteOne(job, key);
                    return;
                }

                if (replacing)
                {
                    WriteSwapMeta(trashRoot!, key, row.Title, row.Owner, oldPrimary!);
                    AddLog(job, $"⇅ {key} {outcome.FileCount} 文件 / {outcome.Bytes / 1024 / 1024} MB：" +
                                $"手机 q{row.PhoneQuality}/{QualityRank.CodecName(row.PhoneCodecid)} 取代库里 " +
                                $"q{oldPrimary!.Quality}/{QualityRank.CodecName(oldPrimary.Codecid)}，旧副本已进回收站");
                }
                else
                {
                    AddLog(job, $"✓ {key} {outcome.FileCount} 文件 / {outcome.Bytes / 1024 / 1024} MB（{commitPhase}，{reg.Reason}，{reg.Path}）");
                }
                FinishMeter();
                CompleteOne(job, key);
            }

            if (job.State == "running") job.State = "done";
            job.Message = $"同步结束：{job.Done}/{job.Total}，{job.BytesDone / 1024 / 1024} MB，{sw.Elapsed.TotalSeconds:F0}s";
            job.EndedAt = DateTime.UtcNow;
            AddLog(job, job.Message);
            Publish(job);
        });
    }

    /// <summary>
    /// 下载结果。Upgraded=true 表示升档下载（同档不可下、最高档 ≥ 手机档位）：
    /// 字节必然 ≠ 手机缓存，不做手机 md5 比对，改按 playurl 声明 size 校验完整性；
    /// ActualQuality/ActualCodecid/VideoMd5/AudioMd5 是**实际落盘**的账目（入库时覆盖手机账目）。
    /// </summary>
    /// <summary>
    /// 单个条目自己的字节账目。下载模式 3 条并发时**绝不能共用** JobSnapshot 上的
    /// CurrentBytes*，否则三条的增量会叠到一个分母上，显示出「已下 346 MB / 文件 216 MB」。
    /// </summary>
    private sealed class ItemMeter(long total)
    {
        public long Total { get; set; } = total;
        public long Done { get; set; }
    }

    private sealed record Outcome(bool Ok, string? Error, long Bytes, int FileCount,
        int? ActualQuality = null, int? ActualCodecid = null,
        string? VideoMd5 = null, string? AudioMd5 = null,
        long VideoBytes = 0, long AudioBytes = 0, bool Upgraded = false,
        ScanItem? ScanItem = null);

    /// <summary>
    /// 下载替代（S5+升档策略）：媒体两流走 CDN，元数据小文件仍从手机 tar 拉齐（&lt;100 KB，保持与拷贝路径同构的文件集）。
    /// 三路决策：① 同档同编码可下 → 严格路径，video/audio md5 必须等于手机 index.json 声明值（实测字节级一致）；
    /// ② 同档不可下但最高档 ≥ 手机档位 → 升档下载最高档（优先手机编码），字节必然 ≠ 手机，改按 playurl 声明 size 校验；
    /// ③ 只有更低档 → 拒绝（绝不下载比手机更差的字节），mode=download 跳过 / auto 回退拷贝。
    /// </summary>
    private async Task<Outcome> DownloadViaDash(
        JobSnapshot job, ItemRow row, string staging, string serial, string phoneRoot,
        IReadOnlyList<ScanFile> expected, IProgress<long> progress,
        Action<long>? onTotalChanged, CancellationToken ct)
    {
        if (row.PhoneQuality is null || row.PhoneCodecid is null)
            return new Outcome(false, "手机侧档位未知，无法定位下载流", 0, 0);

        var videoName = expected.FirstOrDefault(f => Roles.FromName(f.Name) == Roles.Video)?.Name;
        var audioName = expected.FirstOrDefault(f => Roles.FromName(f.Name) == Roles.Audio)?.Name;
        if (videoName is null || audioName is null)
            return new Outcome(false, "文件集中找不到 video.m4s / audio.m4s", 0, 0);

        Directory.CreateDirectory(staging);

        // ---- 1) 元数据小文件（entry.json / danmaku.* / cover 等）：tar 排除 m4s 一次拉齐 ----
        // 注意：脚本经 sh -c '...' 包裹，内部禁止单引号，exclude 模式必须用双引号（双引号内不做 glob 展开）
        var metaScript = $"cd \"{phoneRoot}/{row.RelDir}\" || exit 4; tar -cf - --exclude=\"*.m4s\" .";
        var pipe = new System.IO.Pipelines.Pipe(new System.IO.Pipelines.PipeOptions(
            pauseWriterThreshold: 4L << 20, resumeWriterThreshold: 2L << 20,
            useSynchronizationContext: false));
        {
            var producer = Task.Run(async () =>
            {
                try { await _adb.PipeToAsync(serial, metaScript, pipe.Writer.AsStream(), ct); }
                finally { await pipe.Writer.CompleteAsync(); }
            }, ct);
            try
            {
                await using var tar = new System.Formats.Tar.TarReader(pipe.Reader.AsStream(), leaveOpen: false);
                var metaNames = new List<string>();
                while (await tar.GetNextEntryAsync(cancellationToken: ct).ConfigureAwait(false) is { } entry)
                {
                    var name = entry.Name.Replace('\\', '/').Trim();
                    while (name.StartsWith("./")) name = name[2..];
                    // 0 字节的 regular file（如空 danmaku.xml）DataStream 也是 null —— 只跳过目录/链接，
                    // 空文件照样落盘，否则会被误判"元数据缺失"（实测）
                    if (name.Length == 0 || entry.EntryType is not TarEntryType.RegularFile) continue;
                    var full = Path.Combine(staging, name);
                    if (!Path.GetFullPath(full).StartsWith(Path.GetFullPath(staging) + Path.DirectorySeparatorChar))
                        throw new InvalidOperationException($"tar 条目越界：{entry.Name}");
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    await using var fs = File.Create(full);
                    if (entry.DataStream is not null)
                        await entry.DataStream.CopyToAsync(fs, ct).ConfigureAwait(false);
                    progress.Report(entry.Length);
                    metaNames.Add(name);
                }
                await producer.ConfigureAwait(false);
                var metaExpected = expected.Where(f => f.Name != videoName && f.Name != audioName)
                    .Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
                var missing = metaExpected.Except(metaNames).ToList();
                if (missing.Count > 0)
                    return new Outcome(false, $"元数据缺失：{string.Join(",", missing)}", 0, 0);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // 生产者（adb/tar）异常才是根因：空/截断流会让 TarReader 先抛
                // EndOfStreamException，把真实原因（如脚本引号违规）盖掉
                var msg = ex.Message;
                try { await producer.ConfigureAwait(false); }
                catch (Exception pex) { msg = $"{pex.Message}（原 reader 异常：{ex.GetType().Name}）"; }
                return new Outcome(false, $"元数据拉取失败：{msg}", 0, 0);
            }
        }

        // ---- 2) dash 全量流清单：同档 exact 优先（md5 严格闸门）；否则最高档 ≥ 手机档位 → 升档下载；仅更低档 → 拒绝 ----
        var (videos, audios) = await _bili.DashAsync(
            row.Avid, row.Cid, row.PhoneQuality.Value, row.PhoneCodecid.Value, ct).ConfigureAwait(false);

        string vMd5, aMd5;
        long vBytes, aBytes;
        int regQuality, regCodecid;
        bool upgraded;

        var exactVideo = videos.FirstOrDefault(v =>
            v.Id == row.PhoneQuality.Value && v.Codecid == row.PhoneCodecid.Value);
        // 元数据小文件（entry.json / danmaku / cover）已经在上面拉过并计进进度了
        var metaBytes = expected.Where(f => f.Name != videoName && f.Name != audioName).Sum(f => f.Size);
        if (exactVideo is not null)
        {
            // ---- 3) video.m4s：下载 + 手机账目 md5 闸门（同路径有断点 manifest 则续传）----
            // 分母换成**真实流长**：以前按手机文件集大小算，而 web 转码那份通常比手机缓存大
            // （实测 63 MB vs 55 MB），于是"已下 228 MB / 应有 216 MB · 100%"卡着不动。
            var videoPath = Path.Combine(staging, videoName.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(videoPath + ".resume.json"))
                AddLog(job, $"↻ {row.Key}：检测到 video.m4s 断点，续传");
            var vLen = await _bili.StreamLengthAsync(exactVideo, ct).ConfigureAwait(false);
            // 「同档同编码」不等于「同一份字节」：B 站 web 端下发的 mux 与手机缓存经常差几百字节
            // （实测同一条流：web 217,701,037 B vs 手机 217,705,396 B，md5 必然不符）。
            // 长度都不一样就**不下**：省掉整条 200 MB 白跑。这不是「下载后校验失败」，
            // 而是下载前已经确认只能以手机缓存为准：download 模式跳过，auto 模式回退手机拷贝。
            if (vLen > 0 && row.PhoneVideoSize > 0 && vLen != row.PhoneVideoSize)
                return new Outcome(false,
                    $"同档同编码但字节不同：web {vLen:N0} B ≠ 手机 {row.PhoneVideoSize:N0} B" +
                    $"（差 {vLen - row.PhoneVideoSize:+#,0;-#,0;0} B），以手机缓存为准", 0, 0);
            var aLenEst = row.PhoneAudioSize;
            onTotalChanged?.Invoke(vLen > 0 ? metaBytes + vLen + aLenEst : 0);
            (vMd5, vBytes) = await _bili.DownloadStreamAsync(exactVideo, videoPath, progress, vLen, ct).ConfigureAwait(false);
            if (row.PhoneVideoMd5 is { } wantV && vMd5 != wantV)
                return new Outcome(false, $"video md5 不符：期望 {wantV[..12]}…，实际 {vMd5[..12]}…（download_unreliable）", vBytes, 0);

            // ---- 4) audio.m4s：按手机 size 选最近档，md5 不符换档重试 ----
            var audioPath = Path.Combine(staging, audioName.Replace('/', Path.DirectorySeparatorChar));
            var ordered = row.PhoneAudioSize is { } wantSize
                ? audios.OrderBy(a => Math.Abs((long)a.Size - wantSize)).ToList()   // 与手机最接近的档先试
                : audios.ToList();
            string? got = null;
            aBytes = 0;
            foreach (var a in ordered)
            {
                var aLen = await _bili.StreamLengthAsync(a, ct).ConfigureAwait(false);
                if (vLen > 0 && aLen > 0) onTotalChanged?.Invoke(metaBytes + vLen + aLen);
                (got, aBytes) = await _bili.DownloadStreamAsync(a, audioPath, progress, aLen, ct).ConfigureAwait(false);
                if (row.PhoneAudioMd5 is null || got == row.PhoneAudioMd5) break;
                AddLog(job, $"△ audio 档 {a.Id} md5 不符（{got[..12]}…），换下一档重试");
            }
            if (got is null)
                return new Outcome(false, $"audio md5 各档均不符（download_unreliable）", aBytes, 0);
            aMd5 = got;

            regQuality = exactVideo.Id; regCodecid = exactVideo.Codecid; upgraded = false;
            return new Outcome(true, null,
                vBytes + aBytes +
                expected.Where(f => f.Name != videoName && f.Name != audioName).Sum(f => f.Size),
                expected.Count, regQuality, regCodecid, vMd5, aMd5, vBytes, aBytes, upgraded);
        }
        else
        {
            // ---- 升档路径：同档不可下。只有更低档 → 拒绝（策略：绝不下载比手机更差的字节）----
            var maxQ = videos.Max(v => v.Id);
            if (maxQ < row.PhoneQuality.Value)
                return new Outcome(false,
                    $"仅更低档位可下（最高 q{maxQ} < 手机 q{row.PhoneQuality}），按策略必须拷贝", 0, 0);

            // 同最高档内优先手机编码，否则取 dash 首个（同档多编码时网站推荐序）
            var upVideo = videos.Where(v => v.Id == maxQ)
                .OrderByDescending(v => v.Codecid == row.PhoneCodecid.Value).First();
            var upAudio = audios.OrderByDescending(a => a.Size).First();   // 升档音频取最高码率
            AddLog(job, $"△ {row.Key}：同档不可下，改下 q{maxQ}（手机 q{row.PhoneQuality}），按 playurl 声明大小校验");
            lock (_jobLock) { if (job.CurrentKey == row.Key) job.Phase = "upgrade"; }

            // 升档字节比手机文件集大：当前条目进度预期改按**真实流长**。
            // dash 的 size 恒为 0（实测），所以必须自己问一次；
            // 问不到就报 0（UI 只显示"已下 X MB"，不再编一个分母出来）
            var upVLen = await _bili.StreamLengthAsync(upVideo, ct).ConfigureAwait(false);
            var upALen = await _bili.StreamLengthAsync(upAudio, ct).ConfigureAwait(false);
            onTotalChanged?.Invoke(upVLen > 0 && upALen > 0 ? upVLen + upALen + metaBytes : 0);

            var videoPath = Path.Combine(staging, videoName.Replace('/', Path.DirectorySeparatorChar));
            var audioPath = Path.Combine(staging, audioName.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(videoPath + ".resume.json"))
                AddLog(job, $"↻ {row.Key}：检测到 video.m4s 断点，续传");
            (vMd5, vBytes) = await _bili.DownloadStreamAsync(upVideo, videoPath, progress, upVLen, ct).ConfigureAwait(false);
            if (upVLen > 0 && vBytes != upVLen)
                return new Outcome(false, $"video 实际 {vBytes} B != 声明 {upVLen} B（下载不完整）", vBytes, 0);
            (aMd5, aBytes) = await _bili.DownloadStreamAsync(upAudio, audioPath, progress, upALen, ct).ConfigureAwait(false);
            if (upALen > 0 && aBytes != upALen)
                return new Outcome(false, $"audio 实际 {aBytes} B != 声明 {upALen} B（下载不完整）", aBytes, 0);

            regQuality = maxQ; regCodecid = upVideo.Codecid; upgraded = true;
        }

        var totalBytes = vBytes + aBytes +
            expected.Where(f => f.Name != videoName && f.Name != audioName).Sum(f => f.Size);
        return new Outcome(true, null, totalBytes, expected.Count,
            regQuality, regCodecid, vMd5, aMd5, vBytes, aBytes, upgraded);
    }

    /// <summary>整夹/勾选下载时发现的多P条目，回传给收藏夹扫描索引打"多P待确认"标记。</summary>
    public sealed record MultiPItem(long Avid, string Title, int Pages);

    /// <summary>收藏夹下载作业的结果回报：多P跳过清单 + 真正入库成功的 avid（给扫描索引刷新"已同步"）。</summary>
    public sealed record FavJobReport(IReadOnlyList<MultiPItem> MultiP, IReadOnlyList<long> SyncedAvids,
        IReadOnlyList<long>? ChargeAvids = null);

    /// <summary>
    /// 收藏夹直下（S6）：把登录用户收藏夹里的视频直接下载进总库。
    /// 不经过手机：view 拿分P → key 去重（总库已有即跳过）→ dash 最高档（声明 size 校验，
    /// 没有"手机档位"可比）→ 落手机缓存同构目录（合成 entry.json，外部播放器可直接扫）→
    /// UpsertLibraryItem + RegisterBackup(source=download, via=download)。
    /// 复用单作业槽与分段并行/断点续传；失败保留 staging 待续传，size 不符清现场。
    /// <para>多P视频一律跳过（<see cref="MultiPItem"/> 回调上报），要哪个P由用户在 P 选择框里点。</para>
    /// </summary>
    /// <summary>
    /// 存量迁移：补齐「收藏夹直下」条目落盘时漏写的 entry.json 字段。
    ///
    /// 背景（实证）：681 条 download 条目的 entry.json 是极简版，
    /// 缺顶层 title / owner_id / page_data 宽高 —— 外部播放器的 dual 扫描器读到会退化：
    /// 标题变成 key、分辨率 0×0、UP 主合集全部塌进「未识别 UP 主」。
    /// 补齐所需的字段 db 里**全都有**（title/宽高/bvid），只有 owner_id 要发一次 view。
    ///
    /// 安全措施：改之前先把原文备份到 <c>.favsync/entry-backup/&lt;key&gt;.json</c>，
    /// 只备份一次（重复跑不会用补齐后的版本覆盖备份）。
    /// </summary>
    /// <param name="withOwnerMid">true = 顺带补 owner_id（每个 avid 一次 view，681 条约 40 分钟）。</param>
    public string StartBackfill(bool withOwnerMid = false)
    {
        var rows = _db.ListDownloadEntries();
        return Enqueue("backfill", withOwnerMid ? "补齐元数据（含 UP 主 ID）" : "补齐元数据",
        async (job, ct) =>
        {
            job.Total = rows.Count;
            Publish(job);
            AddLog(job, withOwnerMid
                ? "补齐字段：title / owner_id / 宽高。owner_id 需要逐个问 B 站（约 40 分钟），可随时取消"
                : "补齐字段：title / 宽高（owner_id 留空，之后可再跑一次带 UP 主 ID 的）");

            var backupRoot = Path.Combine(_cfg.EffectiveArchiveRoot, ".favsync", "entry-backup");
            var writeOpts = new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            var fixedN = 0; var skipN = 0; var midN = 0; var failN = 0;

            foreach (var r in rows)
            {
                if (ct.IsCancellationRequested) { job.State = "cancelled"; break; }
                lock (_jobLock) { job.CurrentKey = r.Key; job.CurrentTitle = r.Title ?? r.Key; }
                job.Done++;

                try
                {
                    var entryPath = Path.Combine(_cfg.EffectiveArchiveRoot,
                        r.RelDir.Replace('/', Path.DirectorySeparatorChar), "entry.json");
                    if (!File.Exists(entryPath)) { skipN++; continue; }

                    var text = await File.ReadAllTextAsync(entryPath, ct).ConfigureAwait(false);
                    var node = System.Text.Json.Nodes.JsonNode.Parse(text)?.AsObject();
                    if (node is null) { AddLog(job, $"△ {r.Key}：entry.json 解析失败，跳过"); failN++; continue; }

                    var changed = false;
                    if (node["title"] is null && !string.IsNullOrEmpty(r.Title))
                    { node["title"] = r.Title; changed = true; }
                    if (node["bvid"] is null && !string.IsNullOrEmpty(r.Bvid))
                    { node["bvid"] = r.Bvid; changed = true; }
                    if (node["page_data"] is System.Text.Json.Nodes.JsonObject pd)
                    {
                        if (pd["width"] is null && r.Width is { } w) { pd["width"] = w; changed = true; }
                        if (pd["height"] is null && r.Height is { } h) { pd["height"] = h; changed = true; }
                    }

                    var mid = r.OwnerMid;
                    if (mid is null && withOwnerMid)
                    {
                        await _bili.Pacer.WaitTurnAsync(ct).ConfigureAwait(false);  // 与探测共用限速器
                        var info = await _bili.ViewAsync(r.Avid, ct).ConfigureAwait(false);
                        mid = info?.OwnerMid;
                        if (mid is { } m2) { _db.SetOwnerMid(r.Key, m2); midN++; }
                    }
                    if (node["owner_id"] is null && mid is { } m) { node["owner_id"] = m; changed = true; }

                    if (!changed) { skipN++; continue; }

                    // 改的是已入库文件 —— 先备份再写，重复跑不会覆盖首次备份
                    Directory.CreateDirectory(backupRoot);
                    var bak = Path.Combine(backupRoot, r.Key.Replace('/', '_').Replace('\\', '_') + ".json");
                    if (!File.Exists(bak)) await File.WriteAllTextAsync(bak, text, ct).ConfigureAwait(false);
                    await File.WriteAllTextAsync(entryPath, node.ToJsonString(writeOpts), ct).ConfigureAwait(false);
                    fixedN++;
                }
                catch (OperationCanceledException) { job.State = "cancelled"; break; }
                catch (Exception ex) { AddLog(job, $"△ {r.Key}：{ex.Message}"); failN++; }

                Publish(job);
            }

            if (job.State != "cancelled") job.State = "done";
            job.Message = $"补齐完成：改写 {fixedN} 条 · 无需改动 {skipN} · 新查到 UP 主 ID {midN} · 失败 {failN}";
            job.EndedAt = DateTime.UtcNow;
            AddLog(job, job.Message + "（原文备份在 .favsync/entry-backup/）");
            Publish(job);
        });
    }

    public string StartFavDownload(long mediaId, string? folderTitle,
        IReadOnlyList<string>? onlyAvids = null, Action<FavJobReport>? onReport = null)
    {
        if (!_bili.HasCookies) throw new InvalidOperationException("B 站未登录，先扫码登录");

        var wantAvids = onlyAvids is { Count: > 0 }
            ? onlyAvids.Select(long.Parse).ToHashSet() : null;

        return Enqueue("fav", string.IsNullOrWhiteSpace(folderTitle) ? "收藏夹下载" : $"收藏夹下载（{folderTitle}）", async (job, ct) =>
        {
            var sw = Stopwatch.StartNew();
            var multiP = new List<MultiPItem>();
            var synced = new List<long>();
            var charge = new HashSet<long>();
            try
            {
                // ---- 1) 分页拉全收藏夹条目（ps=40 上限，1000+ 条的夹少一半往返）----
                job.Message = $"正在读取收藏夹「{folderTitle ?? mediaId.ToString()}」…";
                Publish(job);
                var all = new List<BiliClient.FavItem>();
                int total = 0;
                for (int pn = 1; pn <= 300; pn++)
                {
                    var (t, page) = await _bili.FavItemsAsync(mediaId, pn, 40, ct).ConfigureAwait(false);
                    if (t > 0) total = t;        // 越界页 data:null 会回 0，别覆盖标称数
                    all.AddRange(page);
                    if (page.Count == 0 || all.Count >= total) break;
                    await Task.Delay(150, ct).ConfigureAwait(false);
                }
                var videos = all.Where(i => !i.Invalid).ToList();
                if (wantAvids is { } wa) videos = videos.Where(v => wa.Contains(v.Avid)).ToList();

                // 整夹下载时先按总库现状剔一遍。 avid 粒度：该视频有任意一个分P已在库就算已同步
                // （多P只下了选中的P也视为已同步，剩下的P不推）——与扫描索引口径一致
                var lib = _db.BackupIndex();
                var before = videos.Count;
                videos = videos.Where(v => !lib.Avids.Contains(v.Avid)).ToList();
                var already = before - videos.Count;

                // 上一轮已打标的充电视频：再跑必然又是 87008，直接跳过（省一次 view + 一次失败）
                var knownCharge = _db.ChargeAvids();
                var chargeSkip = videos.Count(v => knownCharge.Contains(v.Avid));
                if (chargeSkip > 0) videos = videos.Where(v => !knownCharge.Contains(v.Avid)).ToList();

                job.Total = videos.Count + chargeSkip;
                job.Done = chargeSkip;          // 跳过项也算进度，否则 done 永远差一截
                AddLog(job, $"收藏夹「{folderTitle ?? mediaId.ToString()}」{total} 项，" +
                            $"有效视频 {all.Count(i => !i.Invalid)}，总库已有 {already}，本批待下 {videos.Count}" +
                            $"（已失效 {all.Count(i => i.Invalid)} 项不可下" +
                            (chargeSkip > 0 ? $"，充电跳过 {chargeSkip} 项" : "") + "）");
                if (chargeSkip > 0)
                    AddLog(job, $"   ⚡ 已跳过 {chargeSkip} 条充电专属视频（playurl 87008，需先给 UP 充电才能下载）");
                Publish(job);

                // ---- 2) 逐条下载 ----
                foreach (var fav in videos)
                {
                    if (ct.IsCancellationRequested) { job.State = "cancelled"; break; }

                    job.CurrentKey = fav.Avid.ToString();
                    job.CurrentTitle = fav.Title;
                    job.Phase = "download";
                    job.CurrentBytesTotal = 0;
                    job.CurrentBytesDone = 0;
                    job.Message = $"下载 {fav.Title}";
                    Publish(job);

                    await DownloadFavVideoAsync(job, fav, sw, ct, multiP, synced, charge).ConfigureAwait(false);
                    job.Done++;
                    job.QueuedKeys.Remove(fav.Avid.ToString());
                    Publish(job);
                }

                if (job.State == "running") job.State = "done";
                job.Message = $"收藏夹下载结束：{job.Done}/{job.Total}，{job.BytesDone / 1024 / 1024} MB，{sw.Elapsed.TotalSeconds:F0}s" +
                              (multiP.Count > 0 ? $"，多P跳过 {multiP.Count}（点卡片选P）" : "") +
                              (charge.Count > 0 ? $"，充电打标 {charge.Count}" : "");
                job.EndedAt = DateTime.UtcNow;
                AddLog(job, job.Message);
                if (multiP.Count > 0)
                    foreach (var m in multiP.Take(8))
                        AddLog(job, $"   ⏸ 多P待确认：「{m.Title}」（{m.Pages} 个分P）");
                if (multiP.Count > 8) AddLog(job, $"   … 还有 {multiP.Count - 8} 条多P");
                if (charge.Count > 0)
                {
                    foreach (var t in all.Where(i => charge.Contains(i.Avid)).Select(i => i.Title).Take(8))
                        AddLog(job, $"   ⚡ 充电专属：「{t}」—— 未充电，B站不给流");
                    if (charge.Count > 8) AddLog(job, $"   … 还有 {charge.Count - 8} 条充电视频");
                }
                Publish(job);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                job.State = "cancelled";
                job.EndedAt = DateTime.UtcNow;
                AddLog(job, "作业已取消");
                Publish(job);
            }
            catch (Exception ex)
            {
                job.State = "failed";
                job.EndedAt = DateTime.UtcNow;
                AddLog(job, $"✗ 作业失败：{ex.Message}");
                Publish(job);
            }
            finally
            {
                // 无论成败/取消，把已发生的多P、已入库的 avid、本次新判定的充电视频回报给扫描索引
                if (multiP.Count > 0 || synced.Count > 0 || charge.Count > 0)
                {
                    try { onReport?.Invoke(new FavJobReport(multiP, synced, charge.ToArray())); }
                    catch { /* 索引标记失败不影响作业 */ }
                }
            }
        });
    }

    /// <summary>
    /// 单条收藏夹视频：view → 分P。多P一律跳过（记入 <paramref name="multiP"/>，由用户在 P 选择框确认），
    /// 单P直接下载。异常按条目级失败记日志，不炸整个作业。
    /// </summary>
    private async Task DownloadFavVideoAsync(JobSnapshot job, BiliClient.FavItem fav, Stopwatch sw, CancellationToken ct,
        List<MultiPItem>? multiP = null, List<long>? synced = null, HashSet<long>? charge = null)
    {
        var info = await _bili.ViewAsync(fav.Avid, ct).ConfigureAwait(false);
        if (info is null)
        {
            AddLog(job, $"✗ {fav.Title}：稿件不可见（已失效/权限），跳过");
            return;
        }

        // 充电专属且本账号没播放权：view 就能看出来，不必等 playurl 失败。
        // 实测充电视频的 playurl 有两种：无试看回 87008；有试看回 code=0 + 只有 durl（无 dash）——
        // 后者永远撞不到 87008，光靠下载失败打标会漏标（用户反馈的 8 条正是这类）。
        if (info.UpowerExclusive)
        {
            MarkChargeVideo(job, info.Avid, info.Pages[0].Cid, info.Title, charge);
            return;
        }

        if (info.Pages.Count > 1)
        {
            AddLog(job, $"⏸「{info.Title}」多P（{info.Pages.Count} 个分P），跳过 —— 点卡片可单独选P");
            multiP?.Add(new MultiPItem(info.Avid, info.Title, info.Pages.Count));
            return;
        }
        var ok = await DownloadSelectedPagesAsync(job, info, info.Pages, sw, ct, charge).ConfigureAwait(false);
        if (ok.Count > 0 && synced is not null) synced.Add(info.Avid);
    }

    /// <summary>
    /// 下载给定的一组分P（整夹单P路径与选P作业共用）。已在库的 P 跳过；不动 job.Done，由调用方按口径推进。
    /// 返回真正提交进库的分P所属avid（下载成功且入库），供作业回报扫描索引。
    /// </summary>
    private async Task<HashSet<long>> DownloadSelectedPagesAsync(JobSnapshot job, BiliClient.ViewInfo info,
        IEnumerable<BiliClient.ViewPage> pages, Stopwatch sw, CancellationToken ct, HashSet<long>? charge = null)
    {
        var okAvids = new HashSet<long>();
        foreach (var page in pages)
        {
            ct.ThrowIfCancellationRequested();
            var key = $"{info.Avid}_c{page.Cid}";

            // 总库已有备份（无论来源）就跳过 —— fav 与手机缓存按 key 天然去重
            var existing = _db.Get(key);
            if (existing is { BackupCount: > 0 })
            {
                AddLog(job, $"= {key} 总库已有备份，跳过");
                continue;
            }

            var relDir = $"{info.Avid}/c_{page.Cid}";
            var staging = Path.Combine(_cfg.IncomingRoot, relDir.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(staging);

            var bytesProgress = new Progress<long>(delta =>
            {
                job.BytesDone += delta;
                job.CurrentBytesDone += delta;
                if (sw.Elapsed.TotalMilliseconds > 200)
                {
                    job.SpeedBps = job.BytesDone / Math.Max(0.001, sw.Elapsed.TotalSeconds);
                    Publish(job);
                }
            });

            // 这一P探明的长度先计入作业分母；没下成就撤回去，免得分母虚高把进度压成假象
            var beforeTotal = job.BytesTotal;
            Outcome? outcome = null;
            try
            {
                outcome = await DownloadFavPageAsync(job, info, page, staging, bytesProgress, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                job.BytesTotal = beforeTotal;
                AddLog(job, $"△ {key}：下载失败（{ex.Message}），staging 保留待续传");
                MarkChargeOn87008(job, key, ex.Message, info.Avid, charge);
                continue;
            }

            if (outcome is not { Ok: true })
            {
                job.BytesTotal = beforeTotal;
                // 声明 size 不符等不可靠字节：清现场，绝不入库
                ItemCopier.TryDeleteDirectory(staging);
                AddLog(job, $"✗ {key}：{outcome?.Error}，未入库任何字节");
                MarkChargeOn87008(job, key, outcome?.Error, info.Avid, charge);
                continue;
            }
            var ok = outcome;

            // 入库：item 行（缺才插）→ backup 账目 → 整目录 Move 进总库
            var scanItem = ok.ScanItem!;
            _db.UpsertLibraryItem(scanItem);
            var reg = _db.RegisterBackup(scanItem, _cfg.EffectiveArchiveRoot, source: "download", via: "download");
            if (!reg.Added)
            {
                ItemCopier.TryDeleteDirectory(staging);
                AddLog(job, $"= {key} 未新增条目：{reg.Reason}");
                continue;
            }

            var target = Path.Combine(_cfg.EffectiveArchiveRoot, relDir.Replace('/', Path.DirectorySeparatorChar));
            var commit = ItemCopier.Commit(staging, target);
            if (!commit.Ok)
            {
                AddLog(job, $"✗ {key}：提交失败（{commit.Error}），staging 保留");
                continue;
            }
            AddLog(job, $"✓ {key} {ok.Bytes / 1024 / 1024} MB（q{scanItem.Quality}，来源=库内下载，{reg.Path}）");
            okAvids.Add(info.Avid);
        }
        return okAvids;
    }

    /// <summary>
    /// 下载现场撞上 playurl 87008 = 充电专属视频。用户口径：不做预先探测，
    /// 失败了才打标 —— 这里把结论写进 probe 表（按 avid_cid 落库，服务重启也不丢），
    /// 并记进本次作业的 charge 清单，回报给收藏夹扫描索引 → 前端单独成「充电」桶。
    /// 下次再跑同一个夹，<see cref="StartFavDownload"/> 会直接跳过这些 avid，不再白跑一次。
    /// </summary>
    private void MarkChargeOn87008(JobSnapshot job, string key, string? msg, long avid, HashSet<long>? charge)
    {
        if (string.IsNullOrEmpty(msg) || !msg.Contains("87008")) return;
        _db.SaveProbe(key, ProbeVerdict.Charge, "充电视频：未充电或已过期，B站不给流");
        charge?.Add(avid);
        AddLog(job, $"⚡ {key}：充电专属视频（playurl 87008），已打标 —— 只能充电后下载，或从手机拷贝");
    }

    /// <summary>
    /// 充电专属打标的主动路径：view 里 <c>is_upower_exclusive=true &amp;&amp; is_upower_play=false</c>
    /// 即"稿件是充电可见、本账号没播放权"。下载路径本来就要调 view，所以这是零额外请求的判定。
    /// <para>为什么必须有这条：充电视频的 playurl 有两种表现 —— 无试看回 87008（能打标），
    /// 有试看回 <c>code=0 + 只有 durl 整段 mp4（无 dash）</c>（撞不到 87008 → 老逻辑漏标）。
    /// 用户反馈"8 条未同步全是充电却没进充电桶"就是后者。</para>
    /// </summary>
    private void MarkChargeVideo(JobSnapshot job, long avid, long cid, string? title, HashSet<long>? charge)
    {
        var key = $"{avid}_c{cid}";
        _db.SaveProbe(key, ProbeVerdict.Charge, "充电视频：未充电或已过期，B站不给流");
        charge?.Add(avid);
        AddLog(job, $"⚡ {key}「{title}」：充电专属视频（view.is_upower_exclusive），已打标 —— 需先给 UP 充电，或从手机拷贝");
    }

    /// <summary>
    /// 选P下载：用户在 P 选择框勾了哪些 cid 就下哪些。Total = 选中P数，Done 按P推进；
    /// 已在库的 P 依旧由 <see cref="DownloadSelectedPagesAsync"/> 内部跳过（cid 精确去重）。
    /// </summary>
    public string StartPagesDownload(long mediaId, string? folderTitle,
        IReadOnlyList<(long Avid, long[] Cids)> selections, Action<FavJobReport>? onReport = null)
    {
        if (!_bili.HasCookies) throw new InvalidOperationException("B 站未登录，先扫码登录");
        if (selections.Count == 0) throw new InvalidOperationException("没有选中任何分P");
        var nP = selections.Sum(s => s.Cids.Length);
        if (nP == 0) throw new InvalidOperationException("没有选中任何分P");

        return Enqueue("fav", string.IsNullOrWhiteSpace(folderTitle) ? "选P下载" : $"选P下载（{folderTitle}）", async (job, ct) =>
        {
            var sw = Stopwatch.StartNew();
            var synced = new List<long>();
            var charge = new HashSet<long>();
            try
            {
                job.Total = nP;
                job.Phase = "download";
                AddLog(job, $"选P下载：{selections.Count} 个视频共 {nP} 个分P");
                Publish(job);

                foreach (var (avid, cids) in selections)
                {
                    ct.ThrowIfCancellationRequested();
                    job.CurrentKey = avid.ToString();
                    job.CurrentBytesTotal = 0;
                    job.CurrentBytesDone = 0;

                    var info = await _bili.ViewAsync(avid, ct).ConfigureAwait(false);
                    if (info is null)
                    {
                        AddLog(job, $"✗ {avid}：稿件不可见（已失效/权限），跳过 {cids.Length} P");
                        job.Done += cids.Length;
                        Publish(job);
                        continue;
                    }
                    job.CurrentTitle = info.Title;
                    job.Message = $"下载 {info.Title}";
                    Publish(job);

                    // 同 DownloadFavVideoAsync：充电专属在 view 阶段就能定，别白跑一次 playurl
                    if (info.UpowerExclusive)
                    {
                        MarkChargeVideo(job, info.Avid, info.Pages[0].Cid, info.Title, charge);
                        job.Done += cids.Length;
                        Publish(job);
                        continue;
                    }

                    var cidsSet = cids.ToHashSet();
                    var pages = info.Pages.Where(p => cidsSet.Contains(p.Cid)).ToList();
                    var missing = cids.Length - pages.Count;
                    if (missing > 0)
                        AddLog(job, $"△ {avid}：{missing} 个分P不在稿件里（可能改过P），跳过");

                    // 只回报"真的进了库"的 avid。旧写法是 `pages.Count > 0` 就报 ——
                    // 音频流那一步抛错（实测留下 0 字节 audio.m4s）时整条根本没入库，
                    // 收藏夹页却被标成「已在库」，于是这条再也下不成、每次扫描都挂在待办里
                    // （多P 条目点选 P 下载中途失败时就是这个假象）。
                    var okAvids = await DownloadSelectedPagesAsync(job, info, pages, sw, ct, charge).ConfigureAwait(false);
                    if (okAvids.Contains(avid) && !charge.Contains(avid)) synced.Add(avid);
                    job.Done += pages.Count;
                    job.QueuedKeys.Remove(avid.ToString());
                    Publish(job);
                }

                if (job.State == "running") job.State = "done";
                job.Message = $"选P下载结束：{job.Done}/{job.Total}，{job.BytesDone / 1024 / 1024} MB，{sw.Elapsed.TotalSeconds:F0}s" +
                              (charge.Count > 0 ? $"，充电打标 {charge.Count}" : "");
                job.EndedAt = DateTime.UtcNow;
                AddLog(job, job.Message);
                if (charge.Count > 0)
                    AddLog(job, $"   ⚡ {charge.Count} 条是充电专属视频（playurl 87008），已打标，收藏夹页「充电」桶可查");
                Publish(job);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                job.State = "cancelled";
                job.EndedAt = DateTime.UtcNow;
                AddLog(job, "作业已取消");
                Publish(job);
            }
            catch (Exception ex)
            {
                job.State = "failed";
                job.EndedAt = DateTime.UtcNow;
                AddLog(job, $"✗ 作业失败：{ex.Message}");
                Publish(job);
            }
            finally
            {
                if (synced.Count > 0 || charge.Count > 0)
                {
                    try { onReport?.Invoke(new FavJobReport(Array.Empty<MultiPItem>(), synced, charge.ToArray())); } catch { }
                }
            }
        });
    }

    /// <summary>删单个文件，失败就算了 —— 残留的唯一价值是续传，日志里不必为它报错。</summary>
    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>单个分P：下载最高档 video/audio + 合成 entry.json，字节校验通过返回带 ScanItem 的 Outcome。</summary>
    private async Task<Outcome> DownloadFavPageAsync(JobSnapshot job, BiliClient.ViewInfo info, BiliClient.ViewPage page,
        string staging, IProgress<long> progress, CancellationToken ct)
    {
        var (videos, audios) = await _bili.DashAsync(info.Avid, page.Cid, 120, 12, ct).ConfigureAwait(false);
        if (videos.Count == 0)
            return new Outcome(false, "dash 无视频流（风控降级或老格式），跳过", 0, 0);

        var video = videos.OrderByDescending(v => v.Id)
            .ThenByDescending(v => v.Codecid == 12).First();     // 同档内 HEVC 优先
        var audio = audios.OrderByDescending(a => a.Size).FirstOrDefault(); // 最高码率

        // 进度分母：dash 的 size 恒为 0（B站不填），只能自己探一次真实长度。
        // 探到之后用 knownLength 重载喂回去，下载内部就不用再 HEAD 一遍 —— 净增请求为 0。
        // 探不到（CDN 不给 Content-Length）就保持 0：拿不到分母就不编分母，前端只报"已下多少"。
        var vLen = await _bili.StreamLengthAsync(video, ct).ConfigureAwait(false);
        var aLen = audio is null ? 0 : await _bili.StreamLengthAsync(audio, ct).ConfigureAwait(false);
        job.CurrentBytesTotal = vLen + aLen;
        job.CurrentBytesDone = 0;
        job.BytesTotal += vLen + aLen;      // 作业级分母：边下边探明，所以 ETA 是估算值
        Publish(job);

        var typeTag = video.Id.ToString();
        var relV = $"{typeTag}/video.m4s";
        var relA = $"{typeTag}/audio.m4s";
        Directory.CreateDirectory(Path.Combine(staging, typeTag));

        var videoPath = Path.Combine(staging, relV.Replace('/', Path.DirectorySeparatorChar));
        var (_, vBytes) = await _bili.DownloadStreamAsync(video, videoPath, progress, vLen, ct).ConfigureAwait(false);
        if (video.Size > 0 && vBytes != video.Size)
            return new Outcome(false, $"video 实际 {vBytes} B != 声明 {video.Size} B（下载不完整）", vBytes, 0);
        // dash 的 size 恒为 0（B 站不填），上面那条"声明值"校验对绝大多数流根本不触发 ——
        // 0 字节只有一种解释：这次取流一个字节都没拿到。抛异常（而不是 Outcome(false)）
        // 是为了保住 staging：已下完的部分留着下次续传，删掉等于白费流量。
        if (vBytes <= 0) throw new IOException("video 取回 0 字节（CDN 空响应或节点被限流）");

        long aBytes = 0;
        if (audio is not null)
        {
            var audioPath = Path.Combine(staging, relA.Replace('/', Path.DirectorySeparatorChar));

            async Task<long> PullAudioAsync() =>
                (await _bili.DownloadStreamAsync(audio, audioPath, progress, aLen, ct).ConfigureAwait(false)).Bytes;

            try
            {
                aBytes = await PullAudioAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // 音频是单连接小文件，最容易撞上节点限流/风控（实测：三条稿件都是
                // 视频下完、音频这一步抛错，留下 0 字节 audio.m4s 把整条拖成"没入库"）。
                // 清掉半截字节再试一次；续传清单也要删，否则第二次会拿坏前缀接着写。
                AddLog(job, $"△ {relA}：音频下载失败（{ex.Message}），清掉残留重试一次");
                TryDeleteFile(audioPath);
                TryDeleteFile(audioPath + ".resume.json");
                await Task.Delay(1000, ct).ConfigureAwait(false);
                aBytes = await PullAudioAsync().ConfigureAwait(false);
            }

            if (audio.Size > 0 && aBytes != audio.Size)
                return new Outcome(false, $"audio 实际 {aBytes} B != 声明 {audio.Size} B（下载不完整）", aBytes, 0);
            // 同上：dash 的 audio.Size 通常也是 0，声明校验形同虚设，必须自己卡 0 字节 ——
            // 放过去就会入库一条没有声音的备份，而且它看起来"已同步"，再也没有机会重下。
            if (aBytes <= 0) throw new IOException("audio 取回 0 字节（CDN 空响应或节点被限流）");
        }

        // 合成 entry.json（字段镜像 B 站手机缓存，外部播放器的 dual 扫描器可直接读）
        //
        // 三个字段是外部播放器正确呈现的必要条件，缺一个都会退化：
        //   顶层 title       ← 外部播放器的扫描器只读顶层 title 字段，缺了标题会显示成 key
        //   owner_id         ← UP 主 mid。CollectionRebuilder 按 up_mid 分组合集，
        //                      缺了会导致所有下载件塌进同一个「未识别 UP 主」合集
        //   page_data.*宽高  ← 缺了分辨率显示 0×0
        // 数据全部来自同一次 view（零额外请求），这里只是把它们写进文件。
        var entry = new
        {
            avid = info.Avid,
            bvid = info.Bvid,
            cid = page.Cid,
            title = info.Title,
            page_data = new
            {
                cid = page.Cid,
                page = page.Page,
                part = page.Part,
                title = info.Title,
                width = page.Width ?? 0,
                height = page.Height ?? 0,
            },
            type_tag = typeTag,
            total_time_milli = info.DurationMs,
            total_bytes = vBytes + aBytes,
            has_dash_audio = audio is not null,
            owner_id = info.OwnerMid,
            owner_name = info.Owner,
        };
        var entryPath = Path.Combine(staging, "entry.json");
        await File.WriteAllTextAsync(entryPath,
            System.Text.Json.JsonSerializer.Serialize(entry, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }), ct).ConfigureAwait(false);

        var item = new ScanItem
        {
            Key = $"{info.Avid}_c{page.Cid}",
            Avid = info.Avid,
            Cid = page.Cid,
            Tag = typeTag,
            RelDir = $"{info.Avid}/c_{page.Cid}",
            Title = info.Pages.Count > 1 ? $"{info.Title} P{page.Page} {page.Part}" : info.Title,
            Owner = info.Owner,
            OwnerMid = info.OwnerMid,
            Bvid = info.Bvid,
            Cover = info.Cover,
            Page = page.Page,
            Quality = video.Id,
            Codecid = video.Codecid,
            Width = page.Width,
            Height = page.Height,
            DurationMs = info.DurationMs,
            VideoSize = vBytes,
            AudioSize = aBytes,
        };
        // fav 下载没有手机账目可比，md5 声明留空 —— ContentId 自动退化为 size 组合
        item.Files.Add(new Scan.ScanFile { Name = "entry.json", Role = Scan.Roles.Other, Size = new FileInfo(entryPath).Length });
        item.Files.Add(new Scan.ScanFile { Name = relV, Role = Scan.Roles.Video, Size = vBytes });
        if (audio is not null)
            item.Files.Add(new Scan.ScanFile { Name = relA, Role = Scan.Roles.Audio, Size = aBytes });

        return new Outcome(true, null, item.TotalSize, item.Files.Count, ScanItem: item);
    }

    /// <summary>搬目录。失败时返回 false 并给出原因 —— 换主流程靠它决定要不要回滚。</summary>
    /// <summary>回收站目录名（与 TrashService.SafeName 同一规则，两处要改一起改）。</summary>
    private static string TrashName(string key) =>
        key.Replace('/', '_').Replace('\\', '_').Replace(':', '_');

    /// <summary>
    /// 替换成功后，给回收站里那份旧副本补 meta.json —— 回收站页靠它显示标题/大小，
    /// 没有 meta 就只能看到目录名。写失败不影响同步结果（字节已经安全躺在回收站里）。
    /// </summary>
    private static void WriteSwapMeta(string trashRoot, string key, string? title, string? owner, BackupRow old)
    {
        try
        {
            var meta = new
            {
                key,
                title,
                owner,
                quality = old.Quality,
                codecid = old.Codecid,
                size = old.Size,
                backupCount = 1,
                relDir = old.Path.Replace('\\', '/'),
                deletedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                reason = "被手机那份替换（升档 / 编码更优 / 内容不同）",
            };
            Directory.CreateDirectory(trashRoot);
            File.WriteAllText(Path.Combine(trashRoot, "meta.json"),
                System.Text.Json.JsonSerializer.Serialize(meta));
        }
        catch { /* 回收站元信息写不进去不该影响同步 */ }
    }

    private static bool TryMoveDir(string from, string to, out string? err)
    {
        err = null;
        try
        {
            if (!Directory.Exists(from)) { err = $"源目录不存在：{from}"; return false; }
            if (Directory.Exists(to)) { err = $"目标目录已存在：{to}"; return false; }
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            Directory.Move(from, to);
            return true;
        }
        catch (Exception ex) { err = ex.Message; return false; }
    }

    /// <summary>
    /// 元数据补齐时哪些文件该搬：除 m4s 与 index.json 以外的小文件（entry.json / 弹幕）。
    /// index.json 排除的原因见 ItemCopier.TarScriptMetaOnly —— 它的 md5/size 属于手机字节，
    /// 拷进装网页版 m4s 的目录会让外部播放器判「大小不符 → 损坏」。
    /// </summary>
    private static bool IsTopUpFile(ScanFile f) =>
        f.Role != Scan.Roles.Video && f.Role != Scan.Roles.Audio &&
        !f.Name.EndsWith("index.json", StringComparison.OrdinalIgnoreCase);

    /// <summary>从手机正版 entry.json 里读 UP 主 mid（读不到返回 null）。</summary>
    private static long? TryReadOwnerMid(string entryJsonPath)
    {
        try
        {
            if (!File.Exists(entryJsonPath)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(entryJsonPath));
            if (doc.RootElement.TryGetProperty("owner_id", out var v) &&
                v.ValueKind == System.Text.Json.JsonValueKind.Number)
                return v.GetInt64();
        }
        catch { }
        return null;
    }

    /// <summary>用扫描结果 + 手机文件集重建一个 ScanItem，供 <c>RegisterBackup</c> 使用。</summary>
    private static ScanItem ToScanItem(ItemRow r, IReadOnlyList<ScanFile> files)
    {
        var item = new ScanItem
        {
            Key = r.Key,
            Avid = r.Avid,
            Cid = r.Cid,
            Tag = r.PhoneTag ?? "?",
            RelDir = r.RelDir,
            Title = r.Title,
            Owner = r.Owner,
            OwnerMid = r.OwnerMid,
            Page = r.Page,
            Quality = r.PhoneQuality,
            Codecid = r.PhoneCodecid,
            Width = r.Width,
            Height = r.Height,
            VideoSize = r.PhoneVideoSize,
            VideoMd5 = r.PhoneVideoMd5,
            AudioSize = r.PhoneAudioSize,
            AudioMd5 = r.PhoneAudioMd5,
        };
        item.Files.AddRange(files);
        return item;
    }

    /// <summary>取消当前正在运行的作业。</summary>
    public void Cancel() => _cts?.Cancel();

    /// <summary>取消指定作业：排队中的直接移出队列，运行中的发取消信号。</summary>
    public bool CancelJob(string id)
    {
        lock (_gate)
        {
            var idx = _queue.FindIndex(w => w.Job.Id == id);
            if (idx >= 0)
            {
                var q = _queue[idx].Job;
                _queue.RemoveAt(idx);
                RefreshQueuePositionsLocked();
                q.State = "cancelled";
                q.Message = "已取消（排队中）";
                q.EndedAt = DateTime.UtcNow;
            }
            else if (_running?.Id == id) { _cts?.Cancel(); return true; }
            else return false;
        }
        PublishAll();
        return true;
    }

    private JobSnapshot NewJob(string kind, string label)
    {
        var job = new JobSnapshot { Id = Guid.NewGuid().ToString("N")[..8], Kind = kind, Label = label };
        _jobs[job.Id] = job;
        return job;
    }

    private string ResolveSerial(string? device)
    {
        if (!string.IsNullOrWhiteSpace(device)) return device!;
        if (!string.IsNullOrWhiteSpace(_cfg.Device)) return _cfg.Device!;

        var online = _adb.ListDevices().FirstOrDefault(d => d.IsOnline);
        if (online is null)
            throw new InvalidOperationException("没有在线设备。请先 `adb connect <手机IP>:<端口>` 或插 USB。");
        return online.Serial;
    }

    /// <summary>
    /// 「仅下载替代」的准入判断（mode=download）。
    /// 只放行两类：<br/>
    /// ① <c>exact</c> —— 同档同编码，下载完能用手机 md5 逐字节校验，拿到的就是原件；<br/>
    /// ② <c>lower</c> 但 B 站最高档 <b>高于</b>手机档位 —— 升档下载，内容画质只会更好。<br/>
    /// 拒绝：同画质不同编码（手机 HEVC / B站 AVC·AV1，字节本来就不同，且手机那份是原件）、
    /// 更低画质、已失效、无结论或结论过期。<see cref="Reason"/> 直接写进作业日志给人看。
    /// </summary>
    private static (bool Allow, string Reason) DownloadGate(ItemRow row, DateTime nowUtc)
    {
        var phoneTier = row.PhoneQuality is { } pq ? $"q{pq}" : "未知档位";
        if (row.ProbeVerdict is null)
            return (false, "没有探测记录，先探测再决定能不能下载（本次不下载）");

        if (row.ProbeCheckedAt is { } checkedAt &&
            CatalogDatabase.IsStale(new ProbeRow(row.Key, row.ProbeVerdict, row.ProbeNote, checkedAt), nowUtc))
            return (false, "探测结论已过期，重探后再决定（本次不下载）");

        if (row.ProbeVerdict == ProbeVerdict.Exact) return (true, "");

        if (row.ProbeVerdict == ProbeVerdict.Lower)
        {
            var mq = row.ProbeMaxQuality;
            if (mq is { } m && row.PhoneQuality is { } q && m > q) return (true, "");   // 升档
            if (mq is { } m2 && row.PhoneQuality is { } q2 && m2 == q2)
                return (false,
                    $"B站只给 q{m2} 的其它编码（手机是 {phoneTier}/HEVC），同画质异编码副本不收 —— 手机那份才是原件，改从手机拷贝");
            return (false, $"B站最高只有 q{mq?.ToString() ?? "?"}，低于手机 {phoneTier}，只能从手机拷贝");
        }

        if (row.ProbeVerdict == ProbeVerdict.Gone)
            return (false, "稿件在B站已不可见，只能从手机拷贝");

        // 充电视频：稿件活着，是账号没充电（或充电过期）。playurl 直接回 87008，
        // 下了也是白费 —— 只能从手机拷贝（而且手机缓存若带 DRM，过期后同样解不开，值得你自己过一眼）
        if (row.ProbeVerdict == ProbeVerdict.Charge)
            return (false, "充电视频：未充电或已过期，B站不给流，只能从手机拷贝");

        return (false, $"探测没结论（{row.ProbeNote ?? row.ProbeVerdict}），重探后再决定（本次不下载）");
    }

    private static void AddLog(JobSnapshot job, string line)
    {
        lock (job.Log)
        {
            job.Log.Add($"[{DateTime.Now:HH:mm:ss}] {line}");
            if (job.Log.Count > 500) job.Log.RemoveRange(0, job.Log.Count - 500);
        }
    }

    /// <summary>失败原因要落库给人看，别让一条超长错误把列表撑爆。</summary>
    private static string Clip(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");
}
