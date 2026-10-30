using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using FavSync.Core.Adb;
using FavSync.Core.Bili;
using FavSync.Core.Cold;
using FavSync.Core.Config;
using FavSync.Core.Jobs;
using FavSync.Core.Store;
using Microsoft.AspNetCore.Http.Json;

namespace FavSync.Server;

public sealed class SseHub
{
    private readonly List<System.Threading.Channels.Channel<IReadOnlyList<JobSnapshot>>> _clients = new();
    private readonly object _gate = new();

    public System.Threading.Channels.Channel<IReadOnlyList<JobSnapshot>> Subscribe()
    {
        var ch = System.Threading.Channels.Channel.CreateBounded<IReadOnlyList<JobSnapshot>>(
            new System.Threading.Channels.BoundedChannelOptions(32)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
            });
        lock (_gate) _clients.Add(ch);
        return ch;
    }

    public void Unsubscribe(System.Threading.Channels.Channel<IReadOnlyList<JobSnapshot>> ch)
    {
        lock (_gate) _clients.Remove(ch);
    }

    public void Publish(IReadOnlyList<JobSnapshot> jobs)
    {
        lock (_gate)
        {
            foreach (var c in _clients) c.Writer.TryWrite(jobs);
        }
    }
}

public static partial class Program
{
    public static void Main(string[] args) => CreateApp(args).Run();

    /// <summary>
    /// 构建服务应用。<paramref name="configure"/> 在 Build 之前执行——桌面壳靠它把日志
    /// 重定向到文件（WinExe 没有控制台，Console provider 既看不见又乱码）。
    /// </summary>
    public static WebApplication CreateApp(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
        var builder = WebApplication.CreateBuilder(args);

        var cfg = FavSyncConfig.Load();
        // 铁律：**应用先能打开，再谈配置**。配置里的库地址不可用（盘符不存在/只读/网络盘）时
        // 就地降级到本机兜底目录并留一条 BootNotice，绝不因此启动失败 —— 用户要能看到管理页
        // 才有地方改配置。磁盘上的 config.json 一个字都不动。
        cfg.ResolveArchiveRootForBoot(cfg.ArchiveRoot);
        var checks = Preflight.Run(cfg);
        var adbPath = AdbClient.Resolve(cfg.AdbPath);
        var adb = adbPath is null ? null : new AdbClient(adbPath);

        var db = new CatalogDatabase(cfg.DatabasePath);
        try
        {
            db.EnsureCreated();
        }
        catch (Exception ex)
        {
            // 连兜底目录都建不了库（极罕见：磁盘满/权限），最后一招退到 LocalAppData
            var before = cfg.EffectiveArchiveRoot;
            var emergency = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FavSync", "Library");
            Directory.CreateDirectory(emergency);
            cfg.ArchiveRoot = emergency;
            cfg.BootNotice = $"总库 {before} 打不开数据库（{ex.Message}），本次临时改用 {emergency}。";
            db = new CatalogDatabase(cfg.DatabasePath);
            db.EnsureCreated();
        }

        builder.Services.Configure<JsonOptions>(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            o.SerializerOptions.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        });

        builder.Services.AddSingleton(cfg);
        builder.Services.AddSingleton(checks);
        builder.Services.AddSingleton(db);
        builder.Services.AddSingleton(new SseHub());
        builder.Services.AddSingleton<CoverCache>();
        builder.Services.AddSingleton<PlaybackService>();
        builder.Services.AddSingleton<TrashService>();
        builder.Services.AddSingleton(sp => new BiliClient(sp.GetRequiredService<FavSyncConfig>()));
        builder.Services.AddSingleton<FavScanService>();
        if (adb is not null) builder.Services.AddSingleton(adb);
        if (adb is not null) builder.Services.AddSingleton(sp => new JobManager(
            sp.GetRequiredService<FavSyncConfig>(),
            sp.GetRequiredService<AdbClient>(),
            sp.GetRequiredService<CatalogDatabase>(),
            sp.GetRequiredService<BiliClient>()));

        configure?.Invoke(builder);

        var app = builder.Build();

        if (adb is not null)
        {
            var jobs = app.Services.GetRequiredService<JobManager>();
            var hub = app.Services.GetRequiredService<SseHub>();
            jobs.Updated += hub.Publish;
        }

        app.MapGet("/", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-cache";   // 内嵌在程序集里，版本跟着 exe 走
            return Results.Content(EmbeddedWeb.ReadText("index.html"), "text/html; charset=utf-8");
        });
        app.MapGet("/{file}", (HttpContext ctx, string file) =>
        {
            var content = EmbeddedWeb.Read(file);
            if (content is null) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "no-cache";   // 否则改完前端浏览器还拿旧 js/css
            return Results.Content(content, EmbeddedWeb.ContentType(file));
        });

        MapApi(app);

        Console.WriteLine($"[FavSync] 总库: {cfg.EffectiveArchiveRoot}");
        Console.WriteLine($"[FavSync] 数据库: {cfg.DatabasePath}");
        Console.WriteLine($"[FavSync] 配置: {FavSyncConfig.ConfigPath}");
        Console.WriteLine($"[FavSync] adb: {adbPath ?? "未找到（扫描与同步不可用）"}");
        if (cfg.BootNotice is not null) Console.WriteLine($"[FavSync] ⚠ {cfg.BootNotice}");
        foreach (var c in checks)
            Console.WriteLine($"[预检] {(c.Ok ? "OK  " : "FAIL")} {c.Name} — {c.Detail}");

        return app;
    }

    private static void MapApi(WebApplication app)
    {
        var api = app.MapGroup("/api/v1");

        api.MapGet("/status", (FavSyncConfig cfg, IReadOnlyList<PreflightCheck> checks, CatalogDatabase db, BiliClient bili) =>
        {
            var adb = AdbClient.Resolve(cfg.AdbPath);
            var devices = new List<object>();
            var anyOnline = false;
            if (adb is not null)
            {
                try
                {
                    foreach (var d in new AdbClient(adb).ListDevices())
                    {
                        if (d.IsOnline) anyOnline = true;
                        devices.Add(new { serial = d.Serial, state = d.State, model = d.Model, online = d.IsOnline });
                    }
                }
                catch { }
            }

            var stats = db.Stats();
            var actions = db.SyncActions();
            return Results.Ok(new
            {
                archiveRoot = cfg.EffectiveArchiveRoot,
                // 配置里写的原始值（降级时与 archiveRoot 不同，前端据此提示"临时目录"）
                archiveRootConfigured = cfg.ArchiveRootAsConfigured ?? cfg.ArchiveRoot,
                archiveNotice = cfg.BootNotice,
                configPath = FavSyncConfig.ConfigPath,
                port = cfg.Port,
                databasePath = cfg.DatabasePath,
                phoneRoot = cfg.PhoneRoot,
                adbPath = adb,
                devices,
                checks = checks.Select(c => new { name = c.Name, ok = c.Ok, detail = c.Detail }),
                ready = checks.All(c => c.Ok) && adb is not null && anyOnline,
                stats = new
                {
                    total = stats.Total,
                    onPhone = stats.OnPhone,
                    // 「已同步」= 本次扫描到的手机条目里已入库的，不是全库备份数
                    synced = stats.SyncedOnPhone,
                    syncedInLibrary = stats.Synced,
                    phoneOnly = stats.PhoneOnly,
                    outdated = stats.Outdated,
                    incomplete = stats.Incomplete,
                    mismatch = stats.Mismatch,
                    drifted = stats.Drifted,
                    backups = stats.Backups,
                    bytesOnPhone = stats.BytesOnPhone,
                    bytesBackedUp = stats.BytesBackedUp,
                },
                // 第二行卡片：全部限定在"剩余待同步"（state≠synced）里，下载入库即减
                actions = ActionsPayload(actions),
                lastDevice = cfg.Device,
                biliLoggedIn = bili.HasCookies,
            });
        });

        // ---- 手机连接（无线 adb connect，省得用户手打命令行）----

        api.MapGet("/devices", (FavSyncConfig cfg) =>
        {
            var adb = AdbClient.Resolve(cfg.AdbPath);
            if (adb is null) return Results.Ok(new { adbPath = (string?)null, devices = Array.Empty<object>() });
            var list = new List<object>();
            try
            {
                foreach (var d in new AdbClient(adb).ListDevices())
                    list.Add(new { serial = d.Serial, state = d.State, model = d.Model, online = d.IsOnline });
            }
            catch { }
            return Results.Ok(new { adbPath = adb, devices = list });
        });

        api.MapPost("/devices/connect", (FavSyncConfig cfg, DeviceConnectRequest req) =>
        {
            var adb = AdbClient.Resolve(cfg.AdbPath);
            if (adb is null) return Results.BadRequest(new { error = "未找到 adb，先在设置里配置 adb 路径" });
            var addr = (req.Address ?? string.Empty).Trim();
            if (addr.Length == 0) return Results.BadRequest(new { error = "地址不能为空" });
            try
            {
                var output = new AdbClient(adb).Connect(addr);
                var ok = new AdbClient(adb).ListDevices().Any(d => d.IsOnline);
                return Results.Ok(new { ok, output, address = addr });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        api.MapPost("/devices/disconnect", (FavSyncConfig cfg, DeviceConnectRequest req) =>
        {
            var adb = AdbClient.Resolve(cfg.AdbPath);
            if (adb is null) return Results.BadRequest(new { error = "未找到 adb" });
            try
            {
                var output = new AdbClient(adb).Disconnect(req.Address);
                return Results.Ok(new { output });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // 轻量统计轮询：同步页每 3s 拉一次刷卡片，不走 /status（那边要跑 adb/健康检查）
        api.MapGet("/stats", (CatalogDatabase db) =>
        {
            var st = db.Stats();
            return Results.Ok(new
            {
                stats = new
                {
                    onPhone = st.OnPhone,
                    // 手机侧口径：与 ActionsPayload 的 synced 同义（全库历史见 syncedInLibrary）
                    synced = st.SyncedOnPhone,
                    syncedInLibrary = st.Synced,
                    backups = st.Backups,
                    bytesOnPhone = st.BytesOnPhone,
                    bytesBackedUp = st.BytesBackedUp,
                },
                actions = ActionsPayload(db.SyncActions()),
            });
        });

        api.MapGet("/items", (CatalogDatabase db, JobManager? jobs, string? filter, string? q, int? offset, int? limit) =>
        {
            var (rows, total) = db.Query(filter ?? "all", q, offset ?? 0, Math.Clamp(limit ?? 100, 1, 500));

            // 运行中的同步作业：给列表行附"排队中 / 下载中"进度（仅下载与待下载条目显示）
            var job = jobs?.ActiveJobs.FirstOrDefault(j => j.State == "running");
            // 收窄成非空局部变量，后面三处直接取进度字段（省得一路 CS8602）
            var syncJob = job is { State: "running", Kind: "sync" } ? job : null;

            return Results.Ok(new
            {
                total,
                rows = rows.Select(r => new
                {
                    key = r.Key,
                    avid = r.Avid,
                    cid = r.Cid,
                    title = r.Title,
                    owner = r.Owner,
                    page = r.Page,
                    tag = r.PhoneTag,
                    tagNorm = r.PhoneTagNorm,
                    quality = r.PhoneQuality,
                    codecid = r.PhoneCodecid,
                    size = r.PhoneSize,
                    contentId = r.PhoneContentId,
                    width = r.Width,
                    height = r.Height,
                    fileCount = r.PhoneFileCount,
                    state = r.State,
                    drift = r.Drift,
                    driftNote = r.DriftNote,
                    issueNote = r.IssueNote,
                    issueAt = r.IssueAt,
                    backupCount = r.BackupCount,
                    bestTagNorm = r.BestTagNorm,
                    bestQuality = r.BestQuality,
                    backupSource = r.BackupSource,
                    backupVia = r.BackupVia,
                    onPhone = r.OnPhone,
                    scannedAt = r.ScannedAt,
                    syncedAt = r.SyncedAt,
                    probeVerdict = r.ProbeVerdict,
                    probeNote = r.ProbeNote,
                    probeCheckedAt = r.ProbeCheckedAt,
                    probeMaxQuality = r.ProbeMaxQuality,
                    probeMaxCodecid = r.ProbeMaxCodecid,
                    dlStatus = syncJob != null
                        ? r.Key == syncJob.CurrentKey ? "downloading"
                          : syncJob.QueuedKeys.Contains(r.Key) ? "queued"
                          : null
                        : null,
                    dlDone = syncJob != null && r.Key == syncJob.CurrentKey ? syncJob.CurrentBytesDone : 0,
                    dlTotal = syncJob != null && r.Key == syncJob.CurrentKey ? syncJob.CurrentBytesTotal : 0,
                }),
            });
        });

        api.MapGet("/items/{key}/backups", (CatalogDatabase db, string key) =>
            Results.Ok(new { key, backups = db.ListBackups(key) }));

        // ---------------- 库页（只读视图：总库里真有字节的条目，按入库时间倒序）----------------

        api.MapGet("/library", (CatalogDatabase db, string? q, int? offset, int? limit, string? source) =>
        {
            var (rows, total) = db.QueryLibrary(q, offset ?? 0, Math.Clamp(limit ?? 60, 1, 200), source);
            var counts = db.LibrarySourceCounts();
            return Results.Ok(new
            {
                total,
                source = string.IsNullOrWhiteSpace(source) ? "all" : source,
                counts = new { all = counts.All, backup = counts.Backup, download = counts.Download, other = counts.Other },
                rows = rows.Select(r => new
                {
                    key = r.Key,
                    avid = r.Avid,
                    cid = r.Cid,
                    relDir = r.RelDir,
                    title = r.Title,
                    owner = r.Owner,
                    page = r.Page,
                    durationMs = r.DurationMs,
                    size = r.Size,
                    quality = r.Quality,
                    backupCount = r.BackupCount,
                    source = r.Source,
                    via = r.Via,
                    codecid = r.Codecid,
                    syncedAt = r.SyncedAt,
                    onPhone = r.OnPhone,
                    coverUrl = $"/api/v1/cover/{Uri.EscapeDataString(r.Key)}",
                }),
            });
        });

        // 封面：先查本地缓存，没有再从 B 站拉一次并落盘（之后离线也能看）
        api.MapGet("/cover/{key}", async (CoverCache covers, CatalogDatabase db, string key, CancellationToken ct) =>
        {
            var path = covers.TryLocal(key);
            if (path is null) path = await covers.EnsureAsync(key, db.GetCoverUrl(key), ct);
            return path is null ? Results.NotFound() : Results.File(path, "image/jpeg");
        });

        // 页内播放器：先问一次元信息（标题/UP/编码/同 avid 的其他分 P），再单独预热。
        api.MapGet("/play/{key}/info", (HttpContext ctx, PlaybackService play, CatalogDatabase db, string key) =>
        {
            if (!IsLoopback(ctx)) return Results.Json(new { error = "只能在本机播放" }, statusCode: 403);

            var info = db.GetPlayInfo(key);
            if (info is null) return Results.NotFound(new { error = "总库里没有这个条目" });

            return Results.Ok(new
            {
                key = info.Key,
                avid = info.Avid,
                cid = info.Cid,
                title = info.Title,
                owner = info.Owner,
                page = info.Page,
                durationMs = info.DurationMs,
                size = info.Size,
                quality = info.Quality,
                codecid = info.Codecid,
                backupCount = info.BackupCount,
                cached = play.IsCached(key),
                ffmpeg = PlaybackService.ResolveFfmpeg() is not null,
                player = play.PlayerPath is null ? null : Path.GetFileName(play.PlayerPath),
                siblings = db.Siblings(info.Avid, key)
                    .Select(s => new { key = s.Key, page = s.Page, title = s.Title }),
            });
        });

        // 预热：把双流 remux 进内存。页内播放器先 await 这一步再设 <video src>，
        // 这样加载提示能显示"准备中"，而不是让 <video> 挂着一个几十秒没响应的请求。
        api.MapPost("/play/{key}/prepare", async (HttpContext ctx, PlaybackService play, CatalogDatabase db, string key, CancellationToken ct) =>
        {
            if (!IsLoopback(ctx)) return Results.Json(new { error = "只能在本机播放" }, statusCode: 403);

            var cached = play.IsCached(key);
            var sw = Stopwatch.StartNew();
            var (bytes, error) = await play.GetAsync(key, db, ct);
            if (bytes is null) return Results.BadRequest(new { error });
            return Results.Ok(new { ok = true, bytes = bytes.Length, ms = sw.ElapsedMilliseconds, cached });
        });

        // 双击播放：remux 进内存（磁盘不留缓存），播放器走本机 HTTP(Range) 拉字节。
        // 只允许本机调用 —— 它会启动外部进程，远程可达等于给局域网开了任意程序执行。
        api.MapPost("/play/{key}", async (HttpContext ctx, PlaybackService play, CatalogDatabase db, string key, CancellationToken ct) =>
        {
            if (!IsLoopback(ctx)) return Results.Json(new { error = "只能在本机启动播放器" }, statusCode: 403);

            var (bytes, error) = await play.GetAsync(key, db, ct);
            if (bytes is null) return Results.BadRequest(new { error });

            var url = $"http://127.0.0.1:{ctx.Connection.LocalPort}/api/v1/play/{Uri.EscapeDataString(key)}/stream";
            var player = play.PlayerPath;
            try
            {
                if (player is not null)
                    Process.Start(new ProcessStartInfo(player) { UseShellExecute = false, ArgumentList = { url } });
                else
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });   // 没装播放器就交给系统关联
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = $"启动播放器失败：{ex.Message}" });
            }

            return Results.Ok(new
            {
                ok = true,
                bytes = bytes.Length,
                player = player is null ? "系统默认" : Path.GetFileName(player),
            });
        });

        api.MapGet("/play/{key}/stream", async (HttpContext ctx, PlaybackService play, CatalogDatabase db, string key, CancellationToken ct) =>
        {
            if (!IsLoopback(ctx)) return Results.StatusCode(403);
            var (bytes, error) = await play.GetAsync(key, db, ct);
            if (bytes is null) return Results.BadRequest(new { error });
            return Results.File(bytes, "video/mp4", enableRangeProcessing: true);
        });

        api.MapGet("/media/preflight", (PlaybackService play) => Results.Ok(new
        {
            ffmpeg = PlaybackService.ResolveFfmpeg(),
            player = play.PlayerPath,
        }));

        // 打开某条目在总库里的文件夹（资源管理器）。桌面壳跑在本机，方便直接看 video.m4s/audio.m4s/entry.json。
        // 同 /play 一样只放行 loopback —— 它会启动外部进程，远程可达等于给局域网开了任意程序执行。
        api.MapPost("/open-folder", (HttpContext ctx, PlaybackService play, CatalogDatabase db, OpenFolderRequest req) =>
        {
            if (!IsLoopback(ctx)) return Results.Json(new { error = "只能在本机打开" }, statusCode: 403);
            if (string.IsNullOrWhiteSpace(req.Key)) return Results.BadRequest(new { error = "缺少 key" });

            var dir = play.ResolveDir(req.Key, db);
            if (dir is null) return Results.NotFound(new { error = "总库里没有这个条目" });
            if (!Directory.Exists(dir)) return Results.NotFound(new { error = $"目录不存在（文件可能被移动过）：{dir}" });

            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = false, ArgumentList = { dir } });
                return Results.Ok(new { ok = true, path = dir });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = $"打开失败：{ex.Message}", path = dir });
            }
        });

        // ---- 回收站：删条目不真删字节，搬进 .trash 等用户确认 ----
        api.MapGet("/trash", (TrashService trash) =>
        {
            var items = trash.List();
            return Results.Ok(new
            {
                root = trash.Root,
                total = items.Count,
                bytes = items.Sum(i => i.Size),
                rows = items.Select(i => new
                {
                    key = i.Key, title = i.Title, owner = i.Owner,
                    size = i.Size, deletedAt = i.DeletedAt, backupCount = i.BackupCount,
                }),
            });
        });

        api.MapPost("/trash", (HttpContext ctx, TrashService trash, CatalogDatabase db, TrashRequest req) =>
        {
            if (!IsLoopback(ctx)) return Results.Json(new { error = "只能在本机删除" }, statusCode: 403);
            if (string.IsNullOrWhiteSpace(req.Key)) return Results.BadRequest(new { error = "缺少 key" });

            var (ok, err, moved) = trash.MoveToTrash(req.Key.Trim(), db);
            return ok
                ? Results.Ok(new { ok = true, path = moved })
                : Results.BadRequest(new { error = err });
        });

        // ---- 版本重复（存量清理）：只翻 role='version' 的历史行，现行入库不再产生 ----
        // 一个 key 只留一份字节；这里能查到东西说明库是旧版本升上来的，删掉旧账即可。
        api.MapGet("/versions", (FavSyncConfig cfg, CatalogDatabase db) =>
        {
            var rows = db.ListVersions();
            return Results.Ok(new
            {
                total = rows.Count,
                bytes = rows.Sum(r => r.Size),
                rows = rows.Select(r => new
                {
                    key = r.Key, title = r.Title, owner = r.Owner,
                    tag = r.TagRaw, tagNorm = r.TagNorm, contentId = r.ContentId,
                    quality = r.Quality, codecid = r.Codecid,
                    size = r.Size, videoSize = r.VideoSize, audioSize = r.AudioSize,
                    path = r.Path, createdAt = r.CreatedAt, source = r.Source, via = r.Via,
                    // 主副本档位：用来说明"这份比主副本低/高"，帮用户判断要不要删
                    bestQuality = r.BestQuality,
                    exists = Directory.Exists(Path.Combine(cfg.EffectiveArchiveRoot,
                        r.Path.Replace('/', Path.DirectorySeparatorChar))),
                }),
            });
        });

        // ---- 存量迁移：补齐收藏夹直下条目漏写的 entry.json 字段（可回滚、可选是否查 UP 主 ID）----
        api.MapPost("/maintenance/backfill-entry", (HttpContext ctx, JobManager jobs, BackfillRequest req) =>
        {
            if (!IsLoopback(ctx)) return Results.Json(new { error = "只能在本机执行" }, statusCode: 403);
            var jobId = jobs.StartBackfill(req.WithOwnerMid == true);
            return Results.Ok(new
            {
                jobId,
                message = req.WithOwnerMid == true
                    ? "已开始：补齐 title / owner_id / 宽高（逐个问 B 站，约 40 分钟，可取消）"
                    : "已开始：补齐 title / 宽高（owner_id 留空）",
            });
        });

        api.MapPost("/versions/delete", (HttpContext ctx, FavSyncConfig cfg, CatalogDatabase db, VersionDeleteRequest req) =>
        {
            if (!IsLoopback(ctx)) return Results.Json(new { error = "只能在本机删除" }, statusCode: 403);
            if (string.IsNullOrWhiteSpace(req.Key)) return Results.BadRequest(new { error = "缺少 key" });

            var key = req.Key.Trim();
            var row = db.ListVersions().FirstOrDefault(v =>
                v.Key == key && v.TagNorm == req.TagNorm && v.ContentId == req.ContentId);
            if (row is null) return Results.BadRequest(new { error = "没找到这个附加版本（可能已被删过）" });

            // 顺序铁律：先搬磁盘进 .trash，成功才动库 —— 搬失败时账目还在，不会出现幽灵记录
            var root = cfg.EffectiveArchiveRoot;
            var src = Path.Combine(root, row.Path.Replace('/', Path.DirectorySeparatorChar));
            var trashDir = Path.Combine(root, ".trash", "versions",
                row.Key.Replace('/', '_').Replace('\\', '_'),
                $"{row.TagRaw}@{row.ContentId}");
            string? moved = null;
            if (Directory.Exists(src))
            {
                try
                {
                    Directory.CreateDirectory(trashDir);
                    Directory.Move(src, trashDir);
                    moved = Path.GetRelativePath(root, trashDir).Replace('\\', '/');
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { error = $"移到回收站失败：{ex.Message}（账目未动）" });
                }
            }

            if (!db.DeleteBackupVersion(key, row.TagNorm, row.ContentId))
                return Results.BadRequest(new { error = "账目删除失败（主副本不允许删）" });

            return Results.Ok(new
            {
                ok = true,
                moved,
                message = moved is null
                    ? "仅删除账目（磁盘上本来就没有这个目录）"
                    : $"已移入回收站：{moved}",
            });
        });

        api.MapPost("/trash/empty", (HttpContext ctx, TrashService trash) =>
        {
            if (!IsLoopback(ctx)) return Results.Json(new { error = "只能在本机清空" }, statusCode: 403);
            var (n, bytes) = trash.Empty();
            return Results.Ok(new { ok = true, removed = n, bytes });
        });

        api.MapPost("/scan", (JobManager? jobs, string? device) =>
        {
            if (jobs is null) return Results.BadRequest(new { error = "adb 不可用，无法扫描" });
            try { return Results.Ok(new { jobId = jobs.StartScan(device) }); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        api.MapPost("/sync", (JobManager? jobs, SyncRequest req) =>
        {
            if (jobs is null) return Results.BadRequest(new { error = "adb 不可用，无法同步" });
            try { return Results.Ok(new { jobId = jobs.StartSync(req.Keys, req.Device, req.Mode ?? "auto") }); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // 扫描**总库**（不是手机）：把磁盘上已存在的目录认领入库。
        // 与 /scan 的本质区别：不碰 phone_*、不置 on_phone=0，只写 backup 账目 + 翻 state='synced'。
        // 用途：手工把条目搬进库、换机迁移、库体检。
        api.MapPost("/library/scan", (JobManager? jobs) =>
        {
            if (jobs is null) return Results.BadRequest(new { error = "服务未就绪，无法扫描总库" });
            try { return Results.Ok(new { jobId = jobs.StartLibraryScan() }); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        api.MapPost("/cancel", (JobManager? jobs, string? id) =>
        {
            if (jobs is null) return Results.Ok(new { ok = true });
            if (string.IsNullOrWhiteSpace(id)) { jobs.Cancel(); return Results.Ok(new { ok = true }); }
            return jobs.CancelJob(id) ? Results.Ok(new { ok = true }) : Results.NotFound(new { error = "作业不存在或已结束" });
        });

        // ---------------- 冷备份与恢复（DESIGN-COLD-BACKUP.md）----------------
        // 一条铁律：**密码只在请求体里走一趟**，服务端只把它转给 7z 进程，
        // 不写库、不写 config、不写日志。清单本身就是密码校验器（-mhe=on 错密码列不出文件）。

        api.MapGet("/cold/status", (FavSyncConfig cfg, CatalogDatabase db) =>
        {
            var svc = new ColdService(cfg, db);
            var root = cfg.EffectiveColdRoot;
            var st = db.ColdStatus(root);

            string? rootError = null;
            long free = -1, total = -1;
            if (root is null) rootError = "还没设置冷备份目录";
            else if (cfg.ColdRootConflict(root) is { } conflict) rootError = conflict;
            else
            {
                try
                {
                    if (Directory.Exists(root))
                    {
                        var di = new DriveInfo(Path.GetPathRoot(root)!);
                        free = di.AvailableFreeSpace;
                        total = di.TotalSize;
                    }
                    else rootError = "目录不存在（导出时会自动创建）";
                }
                catch (Exception ex) { rootError = ex.Message; }
            }

            // 本地缺失 = 账上是 packed/uploaded，磁盘上文件却没了（唯一容易踩的坑，必须显式报出来）。
            // 走 30 秒缓存：桶可能躺在网盘挂载目录上，逐个 stat 会把面板拖到几十秒。
            var missing = 0;
            foreach (var b in db.ListBuckets("all", 0, 100000, out _))
            {
                if (b.State is not (ColdState.Packed or ColdState.Uploaded)) continue;
                if (!svc.BucketFileExistsCached(b)) missing++;
            }

            return Results.Ok(new
            {
                uncoldItems = st.UncoldItems,
                uncoldBytes = st.UncoldBytes,
                coldItems = st.ColdItems,
                coldBytes = st.ColdBytes,
                onlyInBucketItems = st.OnlyInBucketItems,
                onlyInBucketBytes = st.OnlyInBucketBytes,
                blockedItems = st.BlockedItems,
                bucketTotal = st.BucketTotal,
                bucketPendingUpload = st.BucketPendingUpload,
                bucketUploaded = st.BucketUploaded,
                bucketLocalMissing = missing,
                bucketExternal = st.ExternalBuckets,
                bucketExternalBytes = st.ExternalBytes,
                bucketBytes = st.BucketBytes,
                pendingUploadBytes = st.PendingUploadBytes,
                lastCheckAt = st.LastCheckAt,
                lastVerifyAt = st.LastVerifyAt,
                lastBatch = st.BatchId,
                coldRoot = root,
                coldRootError = rootError,
                coldFreeBytes = free,
                coldTotalBytes = total,
                driveType = root is not null && Directory.Exists(root) ? DriveTypeOf(root) : null,
                sevenZip = svc.SevenZipExe,
                drives = AllDrives(),
            });
        });

        // 纯计算：还剩多少条 / 多少字节 / 预计几桶。不落库、不写盘。
        api.MapPost("/cold/estimate", (FavSyncConfig cfg, CatalogDatabase db, ColdEstimateRequest? req) =>
        {
            try
            {
                var svc = new ColdService(cfg, db);
                var e = svc.Estimate(req?.TargetMiB ?? cfg.ColdBucketMiB, CancellationToken.None);
                return Results.Ok(new
                {
                    itemCount = e.ItemCount, bytes = e.Bytes, bucketCount = e.BucketCount,
                    blockedCount = e.BlockedCount, blockedBytes = e.BlockedBytes,
                    targetMiB = e.TargetMiB, blockedSample = e.BlockedSample,
                    label = cfg.ColdLabel, origin = cfg.ColdOrigin,
                });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        api.MapPost("/cold/pack", (JobManager? jobs, ColdPackRequest req) =>
        {
            if (jobs is null) return Results.BadRequest(new { error = "服务未就绪" });
            try
            {
                return Results.Ok(new
                {
                    jobId = jobs.StartColdPack(req.Pass ?? "", req.Label ?? "cold", req.Origin ?? "M",
                        req.TargetMiB ?? 2048, req.MaxBuckets ?? 0, req.MaxGiB ?? 0, req.VerifyMd5 ?? false),
                });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        api.MapGet("/cold/buckets", (FavSyncConfig cfg, CatalogDatabase db,
            string? state, int? offset, int? limit) =>
        {
            var svc = new ColdService(cfg, db);
            var rows = db.ListBuckets(state, offset ?? 0, Math.Clamp(limit ?? 20, 1, 200), out var total);
            return Results.Ok(new
            {
                total,
                rows = rows.Select(b => new
                {
                    bucketId = b.BucketId, batchId = b.BatchId, label = b.Label, origin = b.Origin,
                    fileName = b.FileName, bytes = b.Bytes,
                    sha256 = b.Sha256 is { Length: > 16 } s ? s[..16] + "…" : b.Sha256,
                    itemCount = b.ItemCount, targetMiB = b.TargetMiB,
                    state = b.State, stateLabel = ColdState.Label(b.State), note = b.Note,
                    createdAt = b.CreatedAt, packedAt = b.PackedAt, uploadedAt = b.UploadedAt,
                    fileExists = svc.BucketFileExistsCached(b),
                    external = CatalogDatabase.IsExternalDir(b.Dir, cfg.EffectiveColdRoot),
                    dir = b.Dir,
                }),
            });
        });

        api.MapGet("/cold/blocked", (CatalogDatabase db, int? offset, int? limit) =>
        {
            var rows = db.ListBlocks(offset ?? 0, Math.Clamp(limit ?? 20, 1, 200), out var total);
            return Results.Ok(new { total, rows });
        });

        api.MapPost("/cold/bucket/{id}/uploaded", (CatalogDatabase db, string id) =>
        {
            var b = db.GetBucket(id);
            if (b is null) return Results.NotFound(new { error = "桶不存在" });
            db.SetBucketState(id, ColdState.Uploaded);
            ColdService.InvalidateProbeCache();
            return Results.Ok(new { ok = true });
        });

        // 「已上传」与「删本地」合成一个按钮：分开点最容易"删了却忘了标记"（DESIGN-COLD-BACKUP.md §10 第三种情况）
        api.MapPost("/cold/bucket/{id}/uploaded-prune", (FavSyncConfig cfg, CatalogDatabase db, string id) =>
        {
            var b = db.GetBucket(id);
            if (b is null) return Results.NotFound(new { error = "桶不存在" });
            var svc = new ColdService(cfg, db);
            try
            {
                db.SetBucketState(id, ColdState.Uploaded);
                svc.PruneLocal(b);
                return Results.Ok(new { ok = true, message = "已标记上传并删掉本地文件（账目保留：它描述的是云端事实）" });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = "标记成功但删文件失败：" + ex.Message }); }
        });

        api.MapPost("/cold/bucket/{id}/prune", (FavSyncConfig cfg, CatalogDatabase db, string id) =>
        {
            var b = db.GetBucket(id);
            if (b is null) return Results.NotFound(new { error = "桶不存在" });
            try { new ColdService(cfg, db).PruneLocal(b); return Results.Ok(new { ok = true }); }
            catch (Exception ex) { return Results.BadRequest(new { error = "删除失败：" + ex.Message }); }
        });

        // 释放：桶文件丢了又没上传时，把这批字节退回"未冷备"，否则它们永远进不了打包队列
        api.MapPost("/cold/bucket/{id}/release", (CatalogDatabase db, string id) =>
        {
            var b = db.GetBucket(id);
            if (b is null) return Results.NotFound(new { error = "桶不存在" });
            var n = db.ReleaseBucket(id, "用户释放（桶文件丢失/作废）");
            ColdService.InvalidateProbeCache();
            return Results.Ok(new { ok = true, released = n, message = $"{n} 条已退回未冷备，下次导出会重新打包" });
        });

        api.MapPost("/cold/bucket/{id}/verify", async (FavSyncConfig cfg, CatalogDatabase db,
            string id, ColdVerifyRequest? req, CancellationToken ct) =>
        {
            var b = db.GetBucket(id);
            if (b is null) return Results.NotFound(new { error = "桶不存在" });
            try
            {
                var r = await new ColdService(cfg, db).VerifyBucketAsync(b, req?.Pass ?? "", null, ct);
                return Results.Ok(new { ok = r.Ok, message = r.Message, bytes = r.Bytes, sha256 = r.Sha256 });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        api.MapPost("/cold/bucket/{id}/open", (HttpContext ctx, FavSyncConfig cfg, CatalogDatabase db, string id) =>
        {
            if (!IsLoopback(ctx)) return Results.Json(new { error = "只能在本机打开" }, statusCode: 403);
            var b = db.GetBucket(id);
            if (b is null) return Results.NotFound(new { error = "桶不存在" });
            var dir = new ColdService(cfg, db).BucketDir(b);
            if (dir is null || !Directory.Exists(dir)) return Results.NotFound(new { error = "目录不存在" });
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = false, ArgumentList = { dir } });
                return Results.Ok(new { ok = true, path = dir });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // 读清单 + 与本机库对账（恢复向导第二步）。错密码在这里就该失败，不能等解一小时。
        api.MapPost("/cold/preview", async (FavSyncConfig cfg, CatalogDatabase db,
            ColdIoRequest req, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Dir)) return Results.BadRequest(new { error = "缺少目录" });
            try
            {
                var svc = new ColdService(cfg, db);
                var (mf, err) = await svc.ReadManifestAsync(req.Dir!, req.Pass ?? "", ct);
                if (mf is null) return Results.BadRequest(new { error = err });
                var p = svc.Preview(mf, req.Dir!);
                return Results.Ok(p);
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // 导入外来桶：登记桶 + 挂条目 + 补 probe 结论。不搬字节、不改库，可随便跑。
        api.MapPost("/cold/import", async (FavSyncConfig cfg, CatalogDatabase db,
            ColdIoRequest req, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Dir)) return Results.BadRequest(new { error = "缺少目录" });
            try
            {
                var r = await new ColdService(cfg, db).ImportAsync(req.Dir!, req.Pass ?? "", ct);
                return Results.Ok(new
                {
                    ok = true, r.Batch, r.Buckets, r.NewBuckets, r.Items, r.Probes, r.ExpiredProbes,
                    message = $"清单 {r.Batch}：{r.Buckets} 个桶（新登记 {r.NewBuckets}）、{r.Items} 条已挂账、"
                            + $"补 probe {r.Probes} 条"
                            + (r.ExpiredProbes > 0 ? $"（{r.ExpiredProbes} 条已过期丢弃）" : ""),
                });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        api.MapPost("/cold/restore", (JobManager? jobs, ColdRestoreRequest req) =>
        {
            if (jobs is null) return Results.BadRequest(new { error = "服务未就绪" });
            if (string.IsNullOrWhiteSpace(req.Dir)) return Results.BadRequest(new { error = "缺少目录" });
            try
            {
                return Results.Ok(new { jobId = jobs.StartColdRestore(req.Dir!, req.Pass ?? "", req.BucketIds) });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // ---------------- B 站登录（S4）----------------

        api.MapGet("/bili/state", async (BiliClient bili, CancellationToken ct) =>
        {
            if (!bili.HasCookies) return Results.Ok(new { loggedIn = false, mid = 0, uname = (string?)null });
            var acc = await bili.WhoAmIAsync(ct);
            return Results.Ok(new { loggedIn = acc.LoggedIn, mid = acc.Mid, uname = acc.Uname });
        });

        api.MapPost("/bili/qr/start", async (BiliClient bili, CancellationToken ct) =>
        {
            try
            {
                var qr = await bili.QrGenerateAsync(ct);
                using var gen = new QRCoder.QRCodeGenerator();
                using var data = gen.CreateQrCode(qr.QrUrl, QRCoder.QRCodeGenerator.ECCLevel.Q);
                var png = new QRCoder.PngByteQRCode(data).GetGraphic(6);
                return Results.Ok(new { qrKey = qr.QrKey, qrImage = Convert.ToBase64String(png) });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = "生成二维码失败：" + ex.Message }); }
        });

        api.MapGet("/bili/qr/poll", async (BiliClient bili, FavSyncConfig cfg, string qrcodeKey, CancellationToken ct) =>
        {
            try
            {
                var poll = await bili.QrPollAsync(qrcodeKey, ct);
                string? state = poll.Code switch
                {
                    0 => "confirmed",
                    86090 => "scanned",
                    86038 => "expired",
                    _ => "waiting",     // 86101 待扫码
                };

                if (poll.Code == 0 && poll.CookieUrl is not null)
                {
                    string? sessdata, jct, dede;
                    (sessdata, jct, dede) = BiliClient.ParseCookieUrl(poll.CookieUrl);
                    if (string.IsNullOrEmpty(sessdata) && poll.CookieUrl.Contains("ticket="))
                    {
                        // 新版流程：url 只带 ticket，需要 GET crossDomain 换取 Set-Cookie
                        (sessdata, jct, dede) = await bili.ExchangeTicketAsync(poll.CookieUrl, ct);
                    }
                    if (string.IsNullOrEmpty(sessdata))
                    {
                        // 诊断：不打印值，只打印 host 与参数名，便于定位 B 站返回格式变化
                        var host = poll.CookieUrl.Contains('/') && poll.CookieUrl.Contains('?')
                            ? poll.CookieUrl[..poll.CookieUrl.IndexOf('?')]
                            : poll.CookieUrl;
                        var paramNames = string.Join(',',
                            (poll.CookieUrl.Contains('?') ? poll.CookieUrl[(poll.CookieUrl.IndexOf('?') + 1)..] : "")
                            .Split('&').Select(p => p.Split('=')[0]));
                        return Results.BadRequest(new
                        {
                            error = $"确认成功但未取到 SESSDATA（host={host}；参数：{paramNames}），请重试",
                        });
                    }
                    cfg.BiliSessdata = sessdata;
                    cfg.BiliJct = jct;
                    cfg.BiliDedeUserId = dede;
                    bili.SyncFromConfig();
                    var acc = await bili.WhoAmIAsync(ct);
                    cfg.BiliUname = acc.Uname;
                    cfg.Save();
                    bili.InvalidateCaches();    // uname 已落 config，重置校验缓存
                }

                return Results.Ok(new
                {
                    state,
                    loggedIn = poll.Code == 0,
                    uname = poll.Code == 0 ? cfg.BiliUname : null,
                });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        api.MapPost("/bili/logout", (BiliClient bili, FavSyncConfig cfg) =>
        {
            cfg.BiliSessdata = null;
            cfg.BiliJct = null;
            cfg.BiliDedeUserId = null;
            cfg.BiliUname = null;
            cfg.Save();
            bili.SyncFromConfig();
            return Results.Ok(new { ok = true });
        });

        // 收藏夹条目的封面（还没入库、没有 key）：只代理 B 站图床，落本地缓存
        api.MapGet("/bili/cover", async (CoverCache covers, string u, CancellationToken ct) =>
        {
            var path = await covers.EnsureByUrlAsync(u, ct);
            return path is null ? Results.NotFound() : Results.File(path, "image/jpeg");
        });

        // ---- 收藏夹直下（S6）----
        api.MapGet("/bili/fav/folders", async (BiliClient bili, CancellationToken ct) =>
        {
            if (!bili.HasCookies) return Results.BadRequest(new { error = "B 站未登录" });
            try { return Results.Ok(new { folders = await bili.FavFoldersAsync(ct) }); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // 夹内条目（一页）；inLib = 总库已有备份（cid 可得时才能判定，否则 false 由下载作业兜底）
        api.MapGet("/bili/fav/items", async (BiliClient bili, CatalogDatabase db, long mediaId, int? pn, int? ps, CancellationToken ct) =>
        {
            try
            {
                var (total, items) = await bili.FavItemsAsync(mediaId, pn ?? 1, Math.Clamp(ps ?? 20, 1, 40), ct);
                return Results.Ok(new
                {
                    total,
                    items = items.Select(i =>
                    {
                        var inLib = i.Cid is { } cid && db.Get($"{i.Avid}_c{cid}") is { BackupCount: > 0 };
                        return new
                        {
                            avid = i.Avid,
                            bvid = i.Bvid,
                            cid = i.Cid,
                            title = i.Title,
                            owner = i.Owner,
                            cover = i.Cover,
                            duration = i.DurationSec,
                            invalid = i.Invalid,
                            inLib,
                        };
                    }),
                });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // ---- 整夹扫描：算"已同步 / 未同步 / 共多少"（不等于作业，走独立后台任务）----
        api.MapPost("/bili/fav/scan", (FavScanService scan, FavScanRequest req) =>
        {
            if (req.MediaId <= 0) return Results.BadRequest(new { error = "缺少 mediaId" });
            return Results.Ok(scan.Start(req.MediaId, req.FolderTitle, req.Force == true));
        });

        // items=1/true 时连同条目明细一起返回（扫描结束后客户端拉一次，量级 ~100KB）
        api.MapGet("/bili/fav/index", (FavScanService scan, long mediaId, string? items) =>
        {
            var withItems = items is "1" or "true";
            var snap = scan.Snapshot(mediaId, withItems);
            return snap is null ? Results.Ok(new { mediaId, state = "none" }) : Results.Ok(snap);
        });

        // 左栏所有收藏夹的角标摘要（只含扫过的夹）
        api.MapGet("/bili/fav/index/summary", (FavScanService scan) => Results.Ok(new { folders = scan.Summary() }));

        api.MapPost("/bili/fav/scan/cancel", (FavScanService scan, long mediaId) =>
        {
            scan.Cancel(mediaId);
            return Results.Ok(new { ok = true });
        });

        api.MapPost("/fav/download", (JobManager? jobs, FavScanService scan, FavDownloadRequest req) =>
        {
            if (jobs is null) return Results.BadRequest(new { error = "作业管理器不可用" });
            try
            {
                // 选P模式：前端 P 选择框提交的精确 cid 清单
                if (req.Pages is { Count: > 0 })
                {
                    var sels = req.Pages.Select(p => (p.Avid, p.Cids)).ToList();
                    return Results.Ok(new { jobId = jobs.StartPagesDownload(req.MediaId, req.FolderTitle, sels,
                        rep => scan.MarkReport(req.MediaId, rep)) });
                }
                // 整夹/勾选模式：多P一律跳过，作业回报后刷新扫描索引（multiP 标记 + 已同步数）
                return Results.Ok(new { jobId = jobs.StartFavDownload(req.MediaId, req.FolderTitle, req.Keys,
                    rep => scan.MarkReport(req.MediaId, rep)) });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // P 选择框数据：稿件分P清单 + 每P是否已在库（cid 精确判）
        // ---- 在线预览：服务端代理 B 站 CDN（浏览器直连会被 Referer 校验挡掉）----

#if DEBUG
        // 仅 Debug 构建：直出 playurl 的流 URL，用于排查「某个视频音频 403」这类 CDN 层面的问题。
        // Release 发布包里不存在这段（不影响生产）。
        api.MapGet("/debug/dash", async (HttpContext ctx, BiliClient bili,
            long avid, long cid, int? qn, CancellationToken ct) =>
        {
            if (!IsLoopback(ctx)) return Results.Json(new { error = "只能在本机" }, statusCode: 403);
            try
            {
                var (videos, audios) = await bili.DashAsync(avid, cid, qn ?? 120, 12, ct);
                object Shape(BiliClient.DashStream s) => new
                {
                    id = s.Id, codecid = s.Codecid, size = s.Size, url = s.Url, alt = s.AltUrls,
                };
                return Results.Ok(new { qn = qn ?? 120, videos = videos.Select(Shape), audios = audios.Select(Shape) });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
#endif

        // 流清单：给前端建 MSE SourceBuffer 用（要精确的 codecs 串）
        api.MapGet("/bili/preview/info", async (HttpContext ctx, BiliClient bili, long avid, long cid, CancellationToken ct) =>
        {
            if (!IsLoopback(ctx)) return Results.Json(new { error = "只能在本机预览" }, statusCode: 403);
            try
            {
                var (videos, audios) = await bili.PreviewAsync(avid, cid, ct);
                // 预览默认取**最低档**且 AVC 优先：秒开、省流量，且浏览器兼容性最稳
                // （web 端还有 AV1，Chrome 能解但老机器软解卡；HEVC 基本解不了）
                var pick = videos.OrderBy(v => v.Id).ThenBy(v => v.Codecid == 7 ? 0 : 1).FirstOrDefault();
                var pickA = audios.OrderBy(a => a.Size).FirstOrDefault();
                return Results.Ok(new
                {
                    avid, cid,
                    videos = videos.Select(v => new
                    {
                        id = v.Id, codecid = v.Codecid, size = v.Size, codecs = v.Codecs,
                        mime = v.MimeType, width = v.Width, height = v.Height, bandwidth = v.Bandwidth,
                    }),
                    audios = audios.Select(a => new
                    {
                        id = a.Id, size = a.Size, codecs = a.Codecs, mime = a.MimeType, bandwidth = a.Bandwidth,
                    }),
                    pickVideoId = pick?.Id, pickVideoCodecid = pick?.Codecid, pickAudioId = pickA?.Id,
                });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // 字节代理：原样转发 Range，前端按块拉来喂 SourceBuffer。
        // 流 URL 走 PreviewAsync 的 10 分钟缓存，分块请求不会重复打 playurl（否则必撞风控）。
        api.MapGet("/bili/preview/stream", async (HttpContext ctx, BiliClient bili,
            long avid, long cid, string track, int id, int? codecid, CancellationToken ct) =>
        {
            if (!IsLoopback(ctx)) return Results.Json(new { error = "只能在本机预览" }, statusCode: 403);
            try
            {
                var (videos, audios) = await bili.PreviewAsync(avid, cid, ct);
                var list = string.Equals(track, "audio", StringComparison.OrdinalIgnoreCase) ? audios : videos;
                var s = list.FirstOrDefault(x => x.Id == id && (codecid is null || x.Codecid == codecid))
                        ?? list.FirstOrDefault(x => x.Id == id);
                if (s is null) return Results.NotFound(new { error = $"没有这一档流（{track} id={id}）" });

                var range = ctx.Request.Headers.Range.Count > 0 ? ctx.Request.Headers.Range.ToString() : null;
                using var resp = await bili.FetchRangeAsync(s.Url, range, ct);

                ctx.Response.StatusCode = (int)resp.StatusCode;
                ctx.Response.ContentType = resp.Content.Headers.ContentType?.ToString()
                                           ?? (track == "audio" ? "audio/mp4" : "video/mp4");
                if (resp.Content.Headers.ContentLength is { } len) ctx.Response.ContentLength = len;
                if (resp.Content.Headers.ContentRange is { } cr) ctx.Response.Headers["Content-Range"] = cr.ToString();
                ctx.Response.Headers["Accept-Ranges"] = "bytes";

                await using var body = await resp.Content.ReadAsStreamAsync(ct);
                await body.CopyToAsync(ctx.Response.Body, ct);
                return Results.Empty;
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        api.MapGet("/bili/fav/pages", async (BiliClient bili, CatalogDatabase db, long avid, CancellationToken ct) =>
        {
            try
            {
                var info = await bili.ViewAsync(avid, ct);
                if (info is null) return Results.NotFound(new { error = "稿件不可见（已失效/权限）" });
                var lib = db.BackupIndex();
                return Results.Ok(new
                {
                    avid = info.Avid,
                    title = info.Title,
                    owner = info.Owner,
                    multi = info.Pages.Count > 1,
                    pages = info.Pages.Select(p => new
                    {
                        cid = p.Cid,
                        page = p.Page,
                        part = p.Part,
                        duration = p.DurationSec,
                        inLib = lib.Keys.Contains($"{info.Avid}_c{p.Cid}"),
                    }),
                });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // 探测作业
        api.MapPost("/probe", (JobManager? jobs, ProbeRequest? req) =>
        {
            if (jobs is null) return Results.BadRequest(new { error = "作业管理器不可用" });
            try { return Results.Ok(new { jobId = jobs.StartProbe(req?.Keys) }); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        /// 探测单条（验收用：人工标注样本直接打这个，不写缓存）
        api.MapGet("/bili/probe-test", async (BiliClient bili, long avid, long cid, int? quality, int? codecid, CancellationToken ct) =>
        {
            try
            {
                var r = await bili.ProbeAsync(avid, cid, quality, codecid, ct: ct);
                return Results.Ok(new
                {
                    verdict = r.Verdict, note = r.Note,
                    maxQuality = r.MaxQuality, maxCodecid = r.MaxCodecid,
                });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        /// 流地址诊断（验收用）：给指定档位/编码，回该流的 baseUrl/backupUrl 与实测长度。
        /// 只读、不落库；用来在服务外复现"平行下载的 md5 和手机缓存不符"到底是转码版本不同还是下载写坏了。
        api.MapGet("/bili/url-test", async (BiliClient bili, long avid, long cid,
            int? quality, int? codecid, string? kind, int? id, CancellationToken ct) =>
        {
            try
            {
                var (videos, audios) = await bili.DashAsync(avid, cid, quality ?? 120, codecid ?? 12, ct);
                var video = (kind ?? "video") == "video"
                    ? videos.FirstOrDefault(v => (id is null || v.Id == id) &&
                        (codecid is null || v.Codecid == codecid))
                    : null;
                var audio = (kind ?? "video") == "audio"
                    ? audios.FirstOrDefault(a => id is null || a.Id == id)
                    : null;
                var pick = video ?? audio;
                if (pick is null) return Results.NotFound(new { error = "没有匹配的流" });
                return Results.Ok(new
                {
                    id = pick.Id, codecid = pick.Codecid, dashSize = pick.Size,
                    measured = await bili.StreamLengthAsync(pick, ct),
                    url = pick.Url,
                    alts = pick.AltUrls ?? Array.Empty<string>(),
                });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        /// dash 流清单（验收用：确认某条稿件当前到底给哪些档位/编码，不下载不写库）
        api.MapGet("/bili/dash-test", async (BiliClient bili, long avid, long cid, int? quality, CancellationToken ct) =>
        {
            try
            {
                var (videos, audios) = await bili.DashAsync(avid, cid, quality ?? 120, 12, ct);
                return Results.Ok(new
                {
                    video = videos.Select(v => new { id = v.Id, codecid = v.Codecid, size = v.Size })
                                  .OrderByDescending(v => v.id).ThenBy(v => v.codecid).ToArray(),
                    audio = audios.Select(a => new { id = a.Id, size = a.Size })
                                  .OrderByDescending(a => a.size).ToArray(),
                });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // 活动作业列表（运行中 + 排队）：浮窗靠它渲染多任务
        api.MapGet("/jobs", (JobManager? jobs) => Results.Ok(new
        {
            jobs = (jobs?.ActiveJobs ?? Array.Empty<JobSnapshot>()).Select(j => j.ToDto()).ToArray(),
        }));

        api.MapGet("/jobs/latest", (JobManager? jobs) =>
            jobs?.Latest is { } s ? Results.Ok(s.ToDto()) : Results.Ok(null));

        api.MapGet("/jobs/{id}", (JobManager? jobs, string id) =>
            jobs?.Get(id) is { } s ? Results.Ok(s.ToDto()) : Results.NotFound());

        api.MapGet("/config", (FavSyncConfig cfg, IReadOnlyList<PreflightCheck> checks, IServiceProvider sp) =>
        {
            var control = sp.GetService<IServerControl>();
            return Results.Ok(new
            {
                // 配置里写的原值（降级时它是"用户的意图"，设置页据此回填，用户才能看见写错的路径）
                archiveRoot = cfg.ArchiveRootAsConfigured ?? cfg.ArchiveRoot,
                effectiveArchiveRoot = cfg.EffectiveArchiveRoot,
                archiveFallback = cfg.BootNotice is not null,
                archiveNotice = cfg.BootNotice,
                defaultArchiveRoot = FavSyncConfig.DefaultArchiveRoot,
                phoneRoot = cfg.PhoneRoot,
                device = cfg.Device,
                adbPath = cfg.AdbPath,
                adbResolved = AdbClient.Resolve(cfg.AdbPath),
                port = cfg.Port,
                configPath = FavSyncConfig.ConfigPath,
                canRestart = control?.CanRestart ?? false,
                checks = checks.Select(c => new { name = c.Name, ok = c.Ok, detail = c.Detail }),
                drives = FixedDrives(),
                // 冷备份：允许移动盘/网络盘，所以单独给一份"所有可用盘"的列表
                coldRoot = cfg.ColdRoot,
                coldRootEffective = cfg.EffectiveColdRoot,
                coldLabel = cfg.ColdLabel,
                coldBucketMiB = cfg.ColdBucketMiB,
                coldOrigin = cfg.ColdOrigin,
                sevenZipPath = cfg.SevenZipPath,
                sevenZipResolved = SevenZip.Resolve(cfg.SevenZipPath),
                // 高级项：都是"留空自动探测"，所以把探测结果一并回传，用户才知道空着到底用了哪个
                ffmpegPath = cfg.FfmpegPath,
                ffmpegResolved = PlaybackService.ResolveFfmpeg(cfg),
                playerPath = cfg.PlayerPath,
                playerResolved = PlaybackService.ResolvePlayer(cfg),
                biliUseProxy = cfg.BiliUseProxy,
                allDrives = AllDrives(),
            });
        });

        api.MapPost("/config", (FavSyncConfig cfg, JobManager? jobs, ConfigUpdate req) =>
        {
            if (jobs is { IsBusy: true })
                return Results.Conflict(new { error = "作业运行中，暂不能修改配置" });

            // ArchiveRoot 特殊：数据库路径在启动时随 cfg 定死，运行中改内存单例会造成
            // "字节写新库、catalog 在旧库"的撕裂状态——只落盘，重启生效
            var restart = false;
            string? normalizedRoot = null;

            if (!string.IsNullOrWhiteSpace(req.ArchiveRoot))
            {
                var wanted = req.ArchiveRoot.Trim();
                if (!FavSyncConfig.IsUsable(wanted, out var reason))
                    return Results.BadRequest(new { error = $"这个目录不能当总库：{reason}" });

                normalizedRoot = Path.GetFullPath(wanted);
                restart = !string.Equals(normalizedRoot, cfg.EffectiveArchiveRoot, StringComparison.OrdinalIgnoreCase);
            }

            var port = cfg.Port;
            if (req.Port is { } p)
            {
                if (p is < 1024 or > 65535)
                    return Results.BadRequest(new { error = "端口要在 1024–65535 之间" });
                port = p;
                if (p != cfg.Port) restart = true;
            }

            // 从磁盘重读再改：不碰内存单例（除 archiveRoot 外的字段下一次作业自然生效）
            var saved = FavSyncConfig.Load();
            if (normalizedRoot is not null)
            {
                saved.ArchiveRoot = normalizedRoot;
                cfg.ArchiveRoot = normalizedRoot;
                cfg.ArchiveRootAsConfigured = normalizedRoot;   // 回填用，别让设置页还显示旧路径
            }
            if (req.PhoneRoot is not null) { cfg.PhoneRoot = req.PhoneRoot; saved.PhoneRoot = req.PhoneRoot; }
            if (req.Device is not null)
            {
                // 空串 = 回到"自动取第一台在线设备"，落库成 null 而不是空字符串
                var dev = req.Device.Trim();
                cfg.Device = dev.Length == 0 ? null : dev;
                saved.Device = cfg.Device;
            }
            if (req.AdbPath is not null)
            {
                var adb = req.AdbPath.Trim();
                saved.AdbPath = adb.Length == 0 ? null : adb;      // 空串 = 恢复自动探测
            }
            if (req.Port is not null) saved.Port = port;

            // ---- 冷备份配置：都不需要重启（只影响下一次打包/恢复）----
            if (req.ColdRoot is not null)
            {
                var cold = req.ColdRoot.Trim();
                if (cold.Length == 0)
                {
                    saved.ColdRoot = null;
                    cfg.ColdRoot = null;
                }
                else
                {
                    if (cfg.ColdRootConflict(cold) is { } conflict)
                        return Results.BadRequest(new { error = conflict });
                    string full;
                    try { full = Path.GetFullPath(cold); }
                    catch (Exception ex) { return Results.BadRequest(new { error = "路径无法解析：" + ex.Message }); }

                    try
                    {
                        Directory.CreateDirectory(full);
                        var probe = Path.Combine(full, ".favsync-write.probe");
                        File.WriteAllText(probe, "ok");
                        File.Delete(probe);
                    }
                    catch (Exception ex) { return Results.BadRequest(new { error = "这个目录不可写：" + ex.Message }); }

                    saved.ColdRoot = full;
                    cfg.ColdRoot = full;
                }
            }
            if (req.ColdLabel is not null)
            {
                var label = new string(req.ColdLabel.Trim()
                    .Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.').ToArray());
                saved.ColdLabel = label.Length == 0 ? "cold" : label;
                cfg.ColdLabel = saved.ColdLabel;
            }
            if (req.ColdBucketMiB is { } mib)
            {
                if (mib is < 64 or > 8192)
                    return Results.BadRequest(new { error = "桶大小要在 64–8192 MiB 之间（2 GiB 是推荐值）" });
                saved.ColdBucketMiB = mib;
                cfg.ColdBucketMiB = mib;
            }
            if (req.ColdOrigin is not null)
            {
                var origin = new string(req.ColdOrigin.Trim()
                    .Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.').ToArray());
                saved.ColdOrigin = origin.Length == 0 ? "M" : origin;
                cfg.ColdOrigin = saved.ColdOrigin;
            }
            if (req.SevenZipPath is not null)
            {
                var sz = req.SevenZipPath.Trim();
                if (sz.Length > 0 && !File.Exists(sz))
                    return Results.BadRequest(new { error = "指定的 7z 不存在：" + sz });
                saved.SevenZipPath = sz.Length == 0 ? null : sz;
                cfg.SevenZipPath = saved.SevenZipPath;
            }

            // ---- 播放与网络（高级项）----
            // ffmpeg / 播放器都是**每次调用时**才解析（PlaybackService 内部静态方法），所以改完即时生效；
            // BiliUseProxy 例外：BiliClient 是单例，HttpClientHandler 在构造时就绑定了，重启才换。
            if (req.FfmpegPath is not null)
            {
                var v = req.FfmpegPath.Trim();
                if (v.Length > 0 && !File.Exists(v))
                    return Results.BadRequest(new { error = "指定的 ffmpeg 不存在：" + v });
                saved.FfmpegPath = v.Length == 0 ? null : v;
                cfg.FfmpegPath = saved.FfmpegPath;
            }
            if (req.PlayerPath is not null)
            {
                var v = req.PlayerPath.Trim();
                if (v.Length > 0 && !File.Exists(v))
                    return Results.BadRequest(new { error = "指定的播放器不存在：" + v });
                saved.PlayerPath = v.Length == 0 ? null : v;
                cfg.PlayerPath = saved.PlayerPath;
            }
            if (req.BiliUseProxy is { } useProxy)
            {
                saved.BiliUseProxy = useProxy;
                cfg.BiliUseProxy = useProxy;
            }

            saved.Save();

            return Results.Ok(new
            {
                ok = true,
                archiveRoot = saved.ArchiveRoot ?? saved.EffectiveArchiveRoot,
                effectiveArchiveRoot = saved.EffectiveArchiveRoot,
                port,
                restartRequired = restart,
            });
        });

        // 一键重启服务（只有桌面壳能自重启；命令行 mfav.exe 会返回 restarting=false）
        api.MapPost("/config/restart", (IServiceProvider sp) =>
        {
            var control = sp.GetService<IServerControl>();
            if (control is not { CanRestart: true })
                return Results.Ok(new
                {
                    restarting = false,
                    error = "当前以命令行方式运行，无法自重启：关掉这个窗口重新启动即可",
                });

            var port = FavSyncConfig.Load().Port;
            var ok = control.RequestRestart();
            return Results.Ok(new { restarting = ok, port });
        });

        static object[] FixedDrives()
        {
            var list = new List<object>();
            try
            {
                foreach (var d in DriveInfo.GetDrives())
                {
                    try
                    {
                        // 只列本地固定盘：移动盘/网络盘当总库会被 Preflight 拒掉，列出来只会误导
                        if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                        list.Add(new
                        {
                            root = d.RootDirectory.FullName,
                            label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? null : d.VolumeLabel,
                            freeGb = Math.Round(d.AvailableFreeSpace / 1024.0 / 1024 / 1024, 1),
                            totalGb = Math.Round(d.TotalSize / 1024.0 / 1024 / 1024, 1),
                        });
                    }
                    catch { /* 单个盘读不到就跳过，不能因此让设置页打不开 */ }
                }
            }
            catch { }
            return list.ToArray();
        }

        /// <summary>
        /// 冷备份可选的全部磁盘：**移动盘、网络盘都能用**（它只顺序写单个归档文件，不碰 SQLite），
        /// 这是与「总库只允许本地固定盘」的关键区别，所以单独列一份，别拿 FixedDrives 顶。
        /// </summary>
        static object[] AllDrives()
        {
            var list = new List<object>();
            try
            {
                foreach (var d in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (!d.IsReady) continue;
                        list.Add(new
                        {
                            root = d.RootDirectory.FullName,
                            label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? null : d.VolumeLabel,
                            type = d.DriveType.ToString(),
                            freeGb = Math.Round(d.AvailableFreeSpace / 1024.0 / 1024 / 1024, 1),
                            totalGb = Math.Round(d.TotalSize / 1024.0 / 1024 / 1024, 1),
                            removable = d.DriveType is DriveType.Removable or DriveType.Network,
                        });
                    }
                    catch { }
                }
            }
            catch { }
            return list.ToArray();
        }

        static string? DriveTypeOf(string dir)
        {
            try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!).DriveType.ToString(); }
            catch { return null; }
        }

        // SSE：所有作业状态变化
        api.MapGet("/events", async (HttpContext ctx, SseHub hub, JobManager? jobs) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers.Connection = "keep-alive";

            var ch = hub.Subscribe();
            try
            {
                // 首帧：当前活动列表（可能为空）
                await WriteEvent(ctx, jobs?.ActiveJobs ?? Array.Empty<JobSnapshot>());

                while (!ctx.RequestAborted.IsCancellationRequested)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
                    timeout.CancelAfter(TimeSpan.FromSeconds(15));
                    try
                    {
                        var snap = await ch.Reader.ReadAsync(timeout.Token);
                        await WriteEvent(ctx, snap);
                    }
                    catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested)
                    {
                        await ctx.Response.WriteAsync(": ping\n\n", ctx.RequestAborted);
                        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                    }
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                hub.Unsubscribe(ch);
            }
        });
    }

    /// <summary>本机访问判定（IPv4/IPv6 环回都算）。会启动外部进程的端点必须过这一关。</summary>
    private static bool IsLoopback(HttpContext ctx) =>
        ctx.Connection.RemoteIpAddress is { } ip && System.Net.IPAddress.IsLoopback(ip);

    private static async Task WriteEvent(HttpContext ctx, IReadOnlyList<JobSnapshot> jobs)
    {
        var json = JsonSerializer.Serialize(new { jobs = jobs.Select(j => j.ToDto()).ToArray() },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await ctx.Response.WriteAsync($"data: {json}\n\n", ctx.RequestAborted);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
    }

    public sealed record SyncRequest(string[] Keys, string? Device, string? Mode);
    /// <summary>探测请求：<c>keys</c> 为空 = 常规（只探过期/没探过的）；给了 = 强制重探这几条。</summary>
    public sealed record ProbeRequest(string[]? Keys);
    public sealed record ConfigUpdate(string? ArchiveRoot, string? PhoneRoot, string? Device, string? AdbPath,
        int? Port, string? ColdRoot, string? ColdLabel, int? ColdBucketMiB, string? ColdOrigin,
        string? SevenZipPath, string? FfmpegPath, string? PlayerPath, bool? BiliUseProxy);
    public sealed record ColdEstimateRequest(int? TargetMiB);
    public sealed record ColdPackRequest(string? Pass, string? Label, string? Origin,
        int? TargetMiB, int? MaxBuckets, int? MaxGiB, bool? VerifyMd5);
    public sealed record ColdVerifyRequest(string? Pass);
    public sealed record ColdIoRequest(string? Dir, string? Pass);
    public sealed record ColdRestoreRequest(string? Dir, string? Pass, string[]? BucketIds);
    public sealed record FavPagesSel(long Avid, long[] Cids);
    public sealed record FavDownloadRequest(long MediaId, string? FolderTitle, string[]? Keys, List<FavPagesSel>? Pages);
    public sealed record FavScanRequest(long MediaId, string? FolderTitle, bool? Force);
    public sealed record TrashRequest(string? Key);
    public sealed record VersionDeleteRequest(string? Key, string? TagNorm, string? ContentId);
    public sealed record BackfillRequest(bool? WithOwnerMid);
    public sealed record DeviceConnectRequest(string? Address);
    public sealed record OpenFolderRequest(string Key);

    /// <summary>
    /// /status 与 /stats 共用的卡片负载。口径：**全部限定手机侧**（on_phone=1），
    /// <c>onPhone == synced + upgraded + remaining</c>；四张可下载性卡互斥，
    /// 加上残片/校验失败正好等于 remaining（前端把这两类放第一行）。
    /// 只在库里、手机侧已删除的条目走 <c>libraryOnly</c>，不混进「已同步」。
    /// </summary>
    internal static object ActionsPayload(SyncActionStats a) => new
    {
        onPhone = a.OnPhone,
        remaining = a.Remaining,
        synced = a.Synced,
        upgraded = a.Upgraded,
        exact = a.Exact,
        gone = a.Gone,
        drifted = a.Drifted,
        lower = a.Lower,
        incomplete = a.Incomplete,
        mismatch = a.Mismatch,
        // 冲突总数（= 有变化 + 校验失败 + 残片）。这三类不算「待同步」：
        // 待办不是"搬过来"，而是"决定留哪份"。第一行对账：onPhone = synced + upgraded + remaining + conflict
        conflict = a.Conflict,
        unprobed = a.Unprobed,
        pendingUnknown = a.PendingUnknown,
        // 充电视频数（已含在 lower 里，单发一份给卡片/筛选条写清"其中 N 条是充电"）
        charge = a.Charge,
        // 手机侧已删除、只剩库里那份；lost = 手机没了库里也没有（正常应为 0）
        libraryOnly = a.LibraryOnly,
        lost = a.Lost,
    };
}

public static class EmbeddedWeb
{
    public static string? Read(string file)
    {
        var name = $"FavSync.Server.wwwroot.{file.Replace('/', '.')}";
        var asm = typeof(EmbeddedWeb).Assembly;
        using var s = asm.GetManifestResourceStream(name);
        if (s is null) return null;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    public static string ReadText(string file) => Read(file) ?? "";

    public static string ContentType(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "application/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".ico" => "image/x-icon",
        _ => "application/octet-stream",
    };
}

public partial class Program { }
