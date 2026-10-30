using System.Collections.Concurrent;
using System.Diagnostics;
using FavSync.Core.Config;
using FavSync.Core.Store;

namespace FavSync.Server;

/// <summary>
/// 封面懒缓存。B 站给的 cover 是外链 URL，直接塞给 &lt;img&gt; 会：①断网全白 ②每张都重新下载。
/// 所以首次访问时拉到本地 <c>&lt;archive&gt;/.favsync/covers/&lt;key&gt;.jpg</c>，之后纯粹本地读。
/// 放在 .favsync 下而不是库根：. 开头的目录外部播放器的扫描器会跳过，不会当成视频源。
/// </summary>
public sealed class CoverCache
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = true })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    private readonly ConcurrentDictionary<string, Task<string?>> _inflight = new(StringComparer.Ordinal);
    private readonly FavSyncConfig _cfg;

    public CoverCache(FavSyncConfig cfg) => _cfg = cfg;

    public string Root => Path.Combine(_cfg.EffectiveArchiveRoot, ".favsync", "covers");

    private static string SafeName(string key) =>
        key.Replace('/', '_').Replace('\\', '_').Replace(':', '_');

    public string? TryLocal(string key)
    {
        var p = Path.Combine(Root, SafeName(key) + ".jpg");
        try { return File.Exists(p) && new FileInfo(p).Length > 0 ? p : null; }
        catch { return null; }
    }

    /// <summary>本地有就返回本地；否则从 <paramref name="url"/> 拉一次并落盘。失败返回 null（前端画占位）。</summary>
    public Task<string?> EnsureAsync(string key, string? url, CancellationToken ct = default)
    {
        var local = TryLocal(key);
        if (local is not null) return Task.FromResult<string?>(local);
        if (string.IsNullOrWhiteSpace(url)) return Task.FromResult<string?>(null);

        // 同一张图并发请求只下一次
        return _inflight.GetOrAdd(key, _ => DownloadAsync(key, url!));
    }

    /// <summary>收藏夹条目还没入库、没有 key：用封面 URL 的哈希当文件名缓存。只允许 B 站图床域名。</summary>
    public Task<string?> EnsureByUrlAsync(string url, CancellationToken ct = default)
    {
        if (!IsBiliImage(url)) return Task.FromResult<string?>(null);
        var name = "url_" + HashName(url);
        var local = TryLocal(name);
        if (local is not null) return Task.FromResult<string?>(local);
        return _inflight.GetOrAdd(name, _ => DownloadAsync(name, url));
    }

    private static bool IsBiliImage(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (u.Scheme != "http" && u.Scheme != "https") return false;
        return u.Host.EndsWith("hdslb.com", StringComparison.OrdinalIgnoreCase) ||
               u.Host.EndsWith("bilibili.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string HashName(string s)
    {
        var bytes = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private async Task<string?> DownloadAsync(string key, string url)
    {
        try
        {
            var u = url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : "https://" + url.TrimStart('/');
            var bytes = await Http.GetByteArrayAsync(u).ConfigureAwait(false);
            if (bytes.Length == 0) return null;

            Directory.CreateDirectory(Root);
            var path = Path.Combine(Root, SafeName(key) + ".jpg");
            var tmp = path + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
            return path;
        }
        catch
        {
            return null;
        }
        finally
        {
            _inflight.TryRemove(key, out _);
        }
    }
}

/// <summary>
/// 库页双击播放。<b>纯内存</b>：双流 remux 成标准 MP4 后整个读进内存，临时文件立刻删除，
/// 播放器通过本机 HTTP（Range）拉字节 —— 磁盘上不留任何缓存，也就不需要清理。
///
/// 为什么必须中转一次临时文件：ffmpeg 的 mp4 muxer 需要可 seek 的输出（要回填 moov）。
/// 实测 fMP4 流式（frag_keyframe+empty_moov）虽然能进内存，但播放器读到的时长只有第一个分片
/// （89 秒的片子显示 5 秒），拖拽失效 —— 不可接受。
/// </summary>
public sealed class PlaybackService
{
    private const int MaxEntries = 2;                          // 最多留几个视频在内存
    private const long MaxTotalBytes = 3L * 1024 * 1024 * 1024; // 或总字节上限，先到先淘汰

    private sealed class Entry
    {
        public required byte[] Bytes { get; init; }
        public DateTime LastAccess { get; set; } = DateTime.UtcNow;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _mem = new(StringComparer.Ordinal);
    private readonly FavSyncConfig _cfg;
    private readonly ILogger<PlaybackService> _log;

    public PlaybackService(FavSyncConfig cfg, ILogger<PlaybackService> log)
    {
        _cfg = cfg;
        _log = log;
        CleanupStaleTempFiles();
    }

    public string? PlayerPath => ResolvePlayer(_cfg);

    public static string TempDir => Path.Combine(Path.GetTempPath(), "favsync-play");

    /// <summary>启动清掉上次崩溃/强杀留下的中转文件（正常路径会在读入内存后立刻删）。</summary>
    private void CleanupStaleTempFiles()
    {
        try
        {
            if (!Directory.Exists(TempDir)) return;
            foreach (var f in Directory.EnumerateFiles(TempDir, "*.mp4"))
            {
                try { File.Delete(f); } catch { }
            }
        }
        catch { }
    }

    public bool IsCached(string key)
    {
        lock (_gate) return _mem.ContainsKey(key);
    }

    /// <summary>
    /// 某个条目在总库里的媒体目录（最高档备份所在的 type_tag 子目录，里面就是 video.m4s / audio.m4s）。
    /// 「打开文件夹」与 remux 共用同一套路径算法，避免两处各拼一次、拼歪了。
    /// </summary>
    public string? ResolveDir(string key, CatalogDatabase db)
    {
        var backups = db.ListBackups(key);
        if (backups.Count == 0) return null;
        var b = backups[0];   // ListBackups 按 quality DESC → 最高档
        return Path.Combine(_cfg.EffectiveArchiveRoot, b.Path.Replace('/', Path.DirectorySeparatorChar), b.TagRaw);
    }

    /// <summary>取（必要时生成）某个条目的完整 MP4 字节。失败返回 null 并给 <paramref name="error"/>。</summary>
    public async Task<(byte[]? Bytes, string? Error)> GetAsync(
        string key, CatalogDatabase db, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_mem.TryGetValue(key, out var hit))
            {
                hit.LastAccess = DateTime.UtcNow;
                return (hit.Bytes, null);
            }
        }

        var dir = ResolveDir(key, db);
        if (dir is null) return (null, "总库里没有这个条目的备份");
        var video = Path.Combine(dir, "video.m4s");
        var audio = Path.Combine(dir, "audio.m4s");
        if (!File.Exists(video))
        {
            // 兜底：目录里随便找一个视频文件（单文件源/非常规布局）
            var alt = Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir).FirstOrDefault(f =>
                    f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase) && !f.EndsWith("audio.m4s", StringComparison.OrdinalIgnoreCase))
                : null;
            if (alt is null) return (null, "备份文件不存在（可能被移动或删除）");
            video = alt;
            audio = null;
        }

        byte[] bytes;
        try
        {
            bytes = await RemuxAsync(video, File.Exists(audio) ? audio : null, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "remux 失败 {Key}", key);
            return (null, ex.Message);
        }

        Put(key, bytes);
        return (bytes, null);
    }

    private void Put(string key, byte[] bytes)
    {
        lock (_gate)
        {
            _mem[key] = new Entry { Bytes = bytes };
            EvictLocked();
        }
    }

    private void EvictLocked()
    {
        while (_mem.Count > MaxEntries || _mem.Values.Sum(v => (long)v.Bytes.Length) > MaxTotalBytes)
        {
            var oldest = _mem.OrderBy(kv => kv.Value.LastAccess).First();
            _mem.Remove(oldest.Key);
        }
    }

    /// <summary>remux → 读入内存 → 删临时文件。临时文件只在磁盘上活几百毫秒到几秒。</summary>
    private static async Task<byte[]> RemuxAsync(string video, string? audio, CancellationToken ct)
    {
        var ff = ResolveFfmpeg()
            ?? throw new InvalidOperationException("找不到 ffmpeg：请安装后加入 PATH，或在配置里指定 ffmpegPath");

        Directory.CreateDirectory(TempDir);
        var tmp = Path.Combine(TempDir, Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            var psi = new ProcessStartInfo(ff)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-loglevel");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(video);
            if (audio is not null) { psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(audio); }
            psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("0:v:0");
            if (audio is not null) { psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("1:a:0"); }
            psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("copy");
            psi.ArgumentList.Add("-movflags"); psi.ArgumentList.Add("+faststart");
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("mp4");
            psi.ArgumentList.Add(tmp);

            using var p = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 ffmpeg");
            string err;
            try
            {
                var errTask = p.StandardError.ReadToEndAsync(ct);
                await p.WaitForExitAsync(ct).ConfigureAwait(false);
                err = await errTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 用户在准备阶段关掉播放器/点了别的视频：别让 ffmpeg 在后台把整片读完
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
                throw;
            }

            if (p.ExitCode != 0 || !File.Exists(tmp))
                throw new InvalidOperationException($"remux 失败（ffmpeg {p.ExitCode}）：{Truncate(err, 200)}");

            return await File.ReadAllBytesAsync(tmp, ct).ConfigureAwait(false);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }   // 崩在中间也不会留垃圾
        }
    }

    private static string Truncate(string s, int n) =>
        string.IsNullOrWhiteSpace(s) ? "无输出" : s.Trim().Length <= n ? s.Trim() : s.Trim()[..n];

    public static string? ResolveFfmpeg(FavSyncConfig? cfg = null)
    {
        var configured = cfg?.FfmpegPath ?? Environment.GetEnvironmentVariable("FAVSYNC_FFMPEG");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

        // PATH 里的 ffmpeg
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var exe = Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (File.Exists(exe)) return exe;
            }
            catch { }
        }

        foreach (var c in new[]
                 {
                     @"C:\ffmpeg\bin\ffmpeg.exe",
                     @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
                 })
        {
            if (File.Exists(c)) return c;
        }
        return null;
    }

    public static string? ResolvePlayer(FavSyncConfig? cfg = null)
    {
        if (!string.IsNullOrWhiteSpace(cfg?.PlayerPath) && File.Exists(cfg.PlayerPath)) return cfg.PlayerPath;

        foreach (var c in new[]
                 {
                     @"C:\Program Files\DAUM\PotPlayer\PotPlayerMini64.exe",
                     @"C:\Program Files (x86)\DAUM\PotPlayer\PotPlayerMini.exe",
                     @"C:\Program Files (x86)\DAUM\PotPlayer\PotPlayerMini64.exe",
                     @"C:\Program Files\mpv\mpv.exe",
                     @"C:\Program Files\VideoLAN\VLC\vlc.exe",
                     @"C:\Program Files (x86)\VideoLAN\VLC\vlc.exe",
                 })
        {
            if (File.Exists(c)) return c;
        }
        return null;
    }
}
