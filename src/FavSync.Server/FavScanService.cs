using System.Text.Json;
using FavSync.Core.Bili;
using FavSync.Core.Config;
using FavSync.Core.Jobs;
using FavSync.Core.Store;

namespace FavSync.Server;

/// <summary>
/// 收藏夹"已同步"索引：把一个收藏夹整夹分页拉一遍，逐条判定总库里有没有。
///
/// 为什么不进作业队列：作业队列是**串行单槽**，扫一个 1000+ 条的夹要几十秒到一两分钟，
/// 会把下载/同步全堵在后面。这里每个收藏夹一个后台任务 + 内存快照，UI 轮询进度；
/// 同一时刻只允许一个扫描真正跑（<see cref="SemaphoreSlim"/>），避免连点几个夹把 B 站接口打满。
///
/// 去重口径与下载作业一致（avid 粒度）：该视频有**任意一个分P**在库就算已同步 ——
/// 多P视频只下选中的P也视为已同步，没选的P不再推荐下载（用户明确要求的口径）。
/// 多P视频的页数由下载作业在 ViewAsync 时发现，经 <see cref="MarkMultiP"/> 回填打标。
/// </summary>
public sealed class FavScanService
{
    /// <summary>过期时长：超过这个时间打开夹会自动重扫（收藏夹是会变的）。</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(10);

    private sealed class Entry
    {
        public long MediaId;
        public string Label = "";
        public string State = "queued";            // queued | running | done | failed
        public int Total;
        public int Scanned;
        public int Synced;
        public int Invalid;
        public int Charge;
        public string? Message;
        public DateTime UpdatedAt = DateTime.UtcNow;
        public CancellationTokenSource Cts = new();
        public List<FavIndexItem> Items = new();
    }

    /// <summary>
    /// 索引条目：状态字段（InLib/Invalid/MultiP）+ 展示字段（Cover/Owner/Duration/Bvid）。
    /// 展示字段是扫描时顺手从收藏夹列表接口拿的（不额外发请求）—— 有了它，
    /// 前端打开夹可以直接用快照渲染网格，不必再逐页 20 条去打 B 站列表接口。
    /// </summary>
    public sealed record FavIndexItem(long Avid, long? Cid, string Title, bool InLib, bool Invalid,
        int? MultiP = null, string? Bvid = null, string? Owner = null, long DurationSec = 0, string? Cover = null,
        bool Charge = false);

    /// <summary>左栏摘要行（含计算出的未同步数）。</summary>
    public sealed record FavSummaryRow(long MediaId, string State, int Total, int Scanned, int Synced,
        int Invalid, int Charge, int Unsynced, DateTime UpdatedAt);

    /// <summary>落盘用的摘要（不含条目明细）：只为了让左栏的"已同步"标签在重启后还在。</summary>
    private sealed class SavedRow
    {
        public long MediaId { get; set; }
        public string State { get; set; } = "done";
        public int Total { get; set; }
        public int Scanned { get; set; }
        public int Synced { get; set; }
        public int Invalid { get; set; }
        public int Charge { get; set; }
        public int Unsynced { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOpt = new() { WriteIndented = false };

    private readonly object _gate = new();
    private readonly Dictionary<long, Entry> _map = new();
    private readonly Dictionary<long, SavedRow> _saved = new();   // 上次运行留下的摘要（新扫描直接覆盖）
    private readonly SemaphoreSlim _gateScan = new(1, 1);
    private readonly BiliClient _bili;
    private readonly CatalogDatabase _db;
    private readonly FavSyncConfig _cfg;

    public FavScanService(BiliClient bili, CatalogDatabase db, FavSyncConfig cfg)
    {
        _bili = bili;
        _db = db;
        _cfg = cfg;
        LoadPersisted();
    }

    /// <summary>摘要落盘位置：跟着总库走（换库地址 = 换一套标签），放在 <c>&lt;库&gt;/.favsync/</c> 里。</summary>
    public string StorePath => Path.Combine(_cfg.EffectiveArchiveRoot, ".favsync", "fav-scan.json");

    private void LoadPersisted()
    {
        try
        {
            if (!File.Exists(StorePath)) return;
            var rows = JsonSerializer.Deserialize<List<SavedRow>>(File.ReadAllText(StorePath), JsonOpt);
            foreach (var r in rows ?? new List<SavedRow>()) _saved[r.MediaId] = r;
        }
        catch { /* 文件坏了就当没有：下次扫完会重写 */ }
    }

    /// <summary>把当前摘要写盘（先写 tmp 再 Move，避免半截文件）。只在扫描结束/作业回报后调用。</summary>
    private void Persist()
    {
        lock (_gate)
        {
            foreach (var e in _map.Values)
            {
                // 只落盘成功的扫描：failed 存下来会让"打开夹"先亮一句上次的失败，比"未扫描"更误导
                if (e.State != "done") continue;
                _saved[e.MediaId] = new SavedRow
                {
                    MediaId = e.MediaId, State = e.State, Total = e.Total, Scanned = e.Scanned,
                    Synced = e.Synced, Invalid = e.Invalid, Charge = e.Charge,
                    Unsynced = Unsynced(e), UpdatedAt = e.UpdatedAt,
                };
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
                var tmp = StorePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_saved.Values.ToList(), JsonOpt));
                File.Move(tmp, StorePath, overwrite: true);
            }
            catch { /* 落盘失败不影响功能，只是重启后标签没了 */ }
        }
    }

    /// <summary>
    /// 发起扫描。<paramref name="force"/> = false 时，已扫过且未过期的直接复用。
    /// 返回当前快照（调用方拿 state 判断是"正在扫"还是"直接可用"）。
    /// </summary>
    public object Start(long mediaId, string? label, bool force)
    {
        Entry? existing;
        lock (_gate)
        {
            _map.TryGetValue(mediaId, out existing);
            if (existing is { State: "running" or "queued" }) return Snapshot(mediaId)!;
            if (!force && existing is { State: "done" } && DateTime.UtcNow - existing.UpdatedAt < FreshFor)
                return Snapshot(mediaId)!;
        }

        var entry = new Entry { MediaId = mediaId, Label = label ?? mediaId.ToString(), State = "queued", Message = "排队中…" };
        lock (_gate)
        {
            if (existing is not null) { try { existing.Cts.Cancel(); } catch { } existing.Cts.Dispose(); }
            _map[mediaId] = entry;
            Evict();
        }

        _ = Task.Run(() => RunAsync(entry));
        return Snapshot(mediaId)!;
    }

    /// <summary>
    /// 给左栏所有收藏夹打角标用：只返回摘要，不含条目明细。
    /// 除了本次运行扫过的，还带上上次运行留下的摘要 —— 否则重启后左栏的"已同步"标签会全部消失。
    /// </summary>
    public object Summary()
    {
        lock (_gate)
        {
            var rows = _map.Values
                .Select(e => new FavSummaryRow(e.MediaId, e.State, e.Total, e.Scanned, e.Synced,
                    e.Invalid, e.Charge, Unsynced(e), e.UpdatedAt))
                .ToList();
            var live = rows.Select(r => r.MediaId).ToHashSet();
            rows.AddRange(_saved.Values
                .Where(s => !live.Contains(s.MediaId))
                .Select(s => new FavSummaryRow(s.MediaId, s.State, s.Total, s.Scanned, s.Synced,
                    s.Invalid, s.Charge, s.Unsynced, s.UpdatedAt)));
            return rows.OrderBy(r => r.MediaId).ToArray();
        }
    }

    /// <summary>单夹快照；<paramref name="withItems"/> 时带条目明细（客户端扫描完成后拉一次）。</summary>
    public object? Snapshot(long mediaId, bool withItems = false)
    {
        lock (_gate)
        {
            if (!_map.TryGetValue(mediaId, out var e)) return null;
            return new
            {
                mediaId = e.MediaId,
                state = e.State,
                label = e.Label,
                total = e.Total,
                scanned = e.Scanned,
                synced = e.Synced,
                invalid = e.Invalid,
                charge = e.Charge,
                unsynced = Unsynced(e),
                message = e.Message,
                updatedAt = e.UpdatedAt,
                items = withItems
                    ? e.Items.Select(i => new
                    {
                        avid = i.Avid, cid = i.Cid, title = i.Title, inLib = i.InLib, invalid = i.Invalid, multiP = i.MultiP,
                        bvid = i.Bvid, owner = i.Owner, duration = i.DurationSec, cover = i.Cover, charge = i.Charge,
                    })
                    : null,
            };
        }
    }

    public void Cancel(long mediaId)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(mediaId, out var e) && e.State is "running" or "queued")
            {
                try { e.Cts.Cancel(); } catch { }
            }
        }
    }

    /// <summary>
    /// 下载作业回报：多P条目打分P数标记（前端显示「多P」徽标，点击弹 P 选择框）；
    /// 真正入库成功的 avid 回填 InLib=true（头部"已同步"数随之 +1，避免"明明下完还推荐下载"）。
    /// 条目不在索引里（扫描缓存过期/evict）就补一行，宁可多一条也不丢"待确认"。
    /// 服务重启后索引清空、标记随之丢失 —— 无妨：再点一次整夹下载会重新发现。
    /// </summary>
    public void MarkReport(long mediaId, JobManager.FavJobReport report)
    {
        // 回报的 SyncedAvids 只是"下载路径认为入了库"。实测出现过假阳
        // （音频流 0 字节、整条没入库却回报 synced），而快照一旦标成"已在库"，
        // 整夹/选P下载会把它预剔除 —— 用户再也下不成，每次扫描都挂在待办里。
        // 所以这里以库为准复核一次；锁外查库，避免持锁做 IO。
        var confirmed = report.SyncedAvids.Where(_db.HasBackupForAvid).ToArray();

        lock (_gate)
        {
            if (!_map.TryGetValue(mediaId, out var e)) return;
            foreach (var m in report.MultiP)
            {
                var idx = e.Items.FindIndex(x => x.Avid == m.Avid);
                if (idx >= 0)
                {
                    var it = e.Items[idx];
                    if (it.MultiP != m.Pages) e.Items[idx] = it with { MultiP = m.Pages };
                }
                else
                {
                    e.Items.Add(new FavIndexItem(m.Avid, null, m.Title, InLib: false, Invalid: false, MultiP: m.Pages));
                }
            }
            foreach (var avid in confirmed)
            {
                var idx = e.Items.FindIndex(x => x.Avid == avid);
                if (idx >= 0)
                {
                    if (!e.Items[idx].InLib)
                    {
                        e.Items[idx] = e.Items[idx] with { InLib = true };
                        e.Synced++;
                    }
                }
                else
                {
                    e.Items.Add(new FavIndexItem(avid, null, "(刚入库)", InLib: true, Invalid: false));
                    e.Scanned++;
                    e.Synced++;
                }
            }
            foreach (var avid in report.ChargeAvids ?? Array.Empty<long>())
            {
                var idx = e.Items.FindIndex(x => x.Avid == avid);
                if (idx >= 0)
                {
                    if (!e.Items[idx].Charge) { e.Items[idx] = e.Items[idx] with { Charge = true }; e.Charge++; }
                }
                else
                {
                    e.Items.Add(new FavIndexItem(avid, null, "(充电视频)", InLib: false, Invalid: false, Charge: true));
                    e.Scanned++;
                    e.Charge++;
                }
            }
            e.Message = $"共 {e.Scanned} 条：已同步 {e.Synced}，未同步 {Unsynced(e)}" +
                        (e.Invalid > 0 ? $"，失效 {e.Invalid}" : "") +
                        (e.Charge > 0 ? $"，充电 {e.Charge}" : "");
            e.UpdatedAt = DateTime.UtcNow;
        }
        Persist();   // 作业回报也改了 synced/charge → 摘要跟着落盘，重启后标签不回退
    }

    /// <summary>
    /// 未同步数：与前端 <c>favBucket()</c> 的优先级完全一致（失效 &gt; 充电 &gt; 多P &gt; 在库/不在库），
    /// 只有"既没失效、也不在库、也不是充电"的才算未同步。
    /// <para>以前写成 <c>Scanned - Invalid - Synced</c>，充电条目会被算进未同步 ——
    /// 于是头部写「未同步 8」而「未同步」标签页 0 条、「充电」8 条，自相矛盾。</para>
    /// </summary>
    private static int Unsynced(Entry e)
    {
        if (e.Items.Count > 0) return e.Items.Count(i => !i.Invalid && !i.InLib && !i.Charge);
        return Math.Max(0, e.Scanned - e.Invalid - e.Synced - e.Charge);
    }

    private async Task RunAsync(Entry e)
    {
        var ct = e.Cts.Token;
        try
        {
            await _gateScan.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            lock (_gate) { e.State = "failed"; e.Message = "已取消"; e.UpdatedAt = DateTime.UtcNow; }
            return;
        }

        try
        {
            lock (_gate) { e.State = "running"; e.Message = "读取收藏夹…"; e.UpdatedAt = DateTime.UtcNow; }

            var lib = _db.BackupIndex();     // 一次拿全，扫 1000 条不重复查库
            var charge = _db.ChargeAvids();  // 已判定的充电视频（probe 表），扫完就能标，不必重下才知道
            var seen = new HashSet<long>();

            for (var pn = 1; pn <= 300; pn++)
            {
                ct.ThrowIfCancellationRequested();
                var (total, page) = await _bili.FavItemsAsync(e.MediaId, pn, 40, ct).ConfigureAwait(false);

                lock (_gate)
                {
                    // 越界页 B 站返回 data:null（total 会变 0）——不能用它覆盖已拿到的标称数
                    if (total > 0) e.Total = total;
                    foreach (var it in page)
                    {
                    if (!seen.Add(it.Avid)) continue;      // 翻页期间夹内变动会重复
                    // avid 粒度：任意分P在库 = 已同步（与下载作业预剔除口径一致）
                    var inLib = lib.Avids.Contains(it.Avid);
                        e.Items.Add(new FavIndexItem(it.Avid, it.Cid, it.Title, inLib, it.Invalid,
                            Bvid: it.Bvid, Owner: it.Owner, DurationSec: it.DurationSec, Cover: it.Cover,
                            Charge: charge.Contains(it.Avid)));
                        e.Scanned++;
                        if (charge.Contains(it.Avid)) e.Charge++;
                        if (it.Invalid) e.Invalid++;
                        else if (inLib) e.Synced++;
                    }
                    e.Message = $"已扫 {e.Scanned}/{e.Total}…";
                    e.UpdatedAt = DateTime.UtcNow;
                }

                if (page.Count == 0 || (total > 0 && e.Scanned >= total)) break;
                await Task.Delay(120, ct).ConfigureAwait(false);
            }

            lock (_gate)
            {
                e.State = "done";
                // 标称数 > 实际能拉到的条数：收藏夹里有 B 站不再返回的条目（私密/失效）
                var gap = e.Total > e.Scanned ? $"（收藏夹标称 {e.Total}，B 站只返回 {e.Scanned}）" : "";
                e.Message = $"共 {e.Scanned} 条：已同步 {e.Synced}，未同步 {Unsynced(e)}" +
                            (e.Invalid > 0 ? $"，失效 {e.Invalid}" : "") +
                            (e.Charge > 0 ? $"，充电 {e.Charge}" : "") + gap;
                e.UpdatedAt = DateTime.UtcNow;
            }
            Persist();   // 扫完（含失败）把摘要落盘：左栏"已同步"标签重启后还在
        }
        catch (OperationCanceledException)
        {
            lock (_gate) { e.State = "failed"; e.Message = "已取消"; e.UpdatedAt = DateTime.UtcNow; }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                e.State = "failed";
                // 中途撞风控时前面几页已经拿到了：说清楚"不完整"，别让人以为一条都没扫到
                e.Message = ex.Message + (e.Scanned > 0 ? $"（已扫 {e.Scanned} 条，结果不完整）" : "");
                e.UpdatedAt = DateTime.UtcNow;
            }
        }
        finally
        {
            _gateScan.Release();
        }
    }

    /// <summary>只保留最近 30 个夹的快照，防止长会话把内存撑大。</summary>
    private void Evict()
    {
        if (_map.Count <= 30) return;
        foreach (var e in _map.Values.OrderBy(v => v.UpdatedAt).Take(_map.Count - 30).ToList())
        {
            if (e.State is "running" or "queued") continue;
            try { e.Cts.Dispose(); } catch { }
            _map.Remove(e.MediaId);
        }
    }
}
