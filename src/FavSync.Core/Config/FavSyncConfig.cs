using System.Text.Json;
using System.Text.Json.Serialization;

namespace FavSync.Core.Config;

/// <summary>
/// 全局配置。UI / CLI 共享同一份文件，避免出现两套"总库在哪"。
/// </summary>
public sealed class FavSyncConfig
{
    /// <summary>总库根目录。null 表示用默认值（见 <see cref="DefaultArchiveRoot"/>）。</summary>
    public string? ArchiveRoot { get; set; }

    /// <summary>adb 可执行文件路径。null 表示自动探测。</summary>
    public string? AdbPath { get; set; }

    /// <summary>默认设备序列号（host:port 或 USB 序列号）。null 表示自动取第一台在线设备。</summary>
    public string? Device { get; set; }

    public int Port { get; set; } = 8720;

    /// <summary>手机缓存根目录（设备内绝对路径）。</summary>
    public string PhoneRoot { get; set; } = "/sdcard/Android/data/tv.danmaku.bili/download";

    /// <summary>ffmpeg 路径（库页双击播放要 remux 双流）。null 表示自动探测。</summary>
    public string? FfmpegPath { get; set; }

    /// <summary>播放器可执行文件路径。null 表示自动探测（PotPlayer → mpv → VLC → 系统默认）。</summary>
    public string? PlayerPath { get; set; }

    // ---- 冷备份（DESIGN-COLD-BACKUP.md）----

    /// <summary>
    /// 冷备份根目录。**可以**在移动盘/网络盘上：它只顺序写单个归档文件，不碰 SQLite。
    /// null = 还没配，导出前必须在设置页指定。
    /// </summary>
    public string? ColdRoot { get; set; }

    /// <summary>
    /// 桶名前缀（默认 cold）。只做人工识别：<c>gone-A-20261026-0007.7z</c>。
    /// 明文出现在网盘文件列表里，所以不要写标题/UP 主名。
    /// </summary>
    public string ColdLabel { get; set; } = "cold";

    /// <summary>桶大小（MiB，默认 2048）。它决定的是**恢复粒度**，改动只影响之后新开的桶。</summary>
    public int ColdBucketMiB { get; set; } = 2048;

    /// <summary>导出机标识（M=本机 / A=A 机），拼进桶名，跨机天然不撞号。</summary>
    public string ColdOrigin { get; set; } = "M";

    /// <summary>7z 可执行文件路径。null 表示自动探测（含程序自带 <c>tools\7z*.exe</c>）。</summary>
    public string? SevenZipPath { get; set; }

    // ---- B 站登录态（扫码登录后写入；只落本机 config，不解密导出）----

    /// <summary>SESSDATA cookie（保持 URL 编码原样）。</summary>
    public string? BiliSessdata { get; set; }

    /// <summary>bili_jct cookie（CSRF token）。</summary>
    public string? BiliJct { get; set; }

    /// <summary>DedeUserID cookie。</summary>
    public string? BiliDedeUserId { get; set; }

    /// <summary>登录昵称（登录时缓存，仅展示用）。</summary>
    public string? BiliUname { get; set; }

    /// <summary>
    /// 是否走系统/环境变量代理访问 B 站。<b>默认 false（直连）</b>：实测同一条流经本机代理
    /// 只有 5 MB/s、直连 24 MB/s，B 站 CDN 在国内直连才是对的。只有确实需要代理出境时才开。
    /// </summary>
    public bool BiliUseProxy { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string ConfigPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FavSync",
        "config.json");

    /// <summary>
    /// 默认总库放在用户盘根，不放程序目录：重装/升级程序时不能连带丢库。
    /// </summary>
    public static string DefaultArchiveRoot
    {
        get
        {
            var root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            return Path.Combine(root ?? "C:\\", "FavSyncLibrary");
        }
    }

    /// <summary>
    /// 启动期的降级说明。非 null 表示"配置里的总库这次没用上，临时换了个能用的位置"。
    /// 只存在于内存，<b>绝不写回配置文件</b>：用户的意图（D:\RelayLib）要留着，等他去设置页改。
    /// </summary>
    [JsonIgnore]
    public string? BootNotice { get; set; }

    /// <summary>
    /// 配置里原本写的库地址（降级前的"用户意图"）。设置页据此回填，用户才能看见自己写错的那个路径并改掉，
    /// 而不是被兜底目录盖住。
    /// </summary>
    [JsonIgnore]
    public string? ArchiveRootAsConfigured { get; set; }

    /// <summary>生效的总库根（已展开为绝对路径）。</summary>
    public string EffectiveArchiveRoot =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(ArchiveRoot) ? DefaultArchiveRoot : ArchiveRoot!);

    /// <summary>
    /// 兜底库位置：本机盘上一定存在的可写目录。用于"配置的总库不可用"和"从没配置过"两种情况。
    /// 优先 C:\FavSyncLibrary（用户能找到、空间通常够），不行退到 LocalAppData。
    /// </summary>
    public static string FallbackArchiveRoot
    {
        get
        {
            foreach (var candidate in new[]
                     {
                         DefaultArchiveRoot,
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                      "FavSync", "Library"),
                     })
            {
                if (IsUsable(candidate, out _, create: false)) return Path.GetFullPath(candidate);
            }

            return Path.GetFullPath(DefaultArchiveRoot);
        }
    }

    /// <summary>
    /// 判断某个目录能不能当总库：本地固定盘（UNC 上 SQLite 文件锁不可靠）、能建 .favsync、能写入。
    /// <paramref name="create"/> 为 false 时只做判定，不真的建目录。
    /// </summary>
    public static bool IsUsable(string path, out string? reason, bool create = true)
    {
        reason = null;
        if (string.IsNullOrWhiteSpace(path)) { reason = "路径为空"; return false; }

        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) { reason = "路径无法解析：" + ex.Message; return false; }

        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            reason = "网络路径（UNC）不能当总库：SQLite 文件锁在 SMB 上不可靠，会静默损坏";
            return false;
        }

        var driveRoot = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(driveRoot)) { reason = "无法识别盘符"; return false; }

        try
        {
            if (!Directory.Exists(driveRoot)) { reason = $"{driveRoot.TrimEnd('\\')} 盘不存在"; return false; }

            var di = new DriveInfo(driveRoot);
            if (di.DriveType != DriveType.Fixed) { reason = $"{driveRoot} 是 {di.DriveType} 盘，要求本地固定盘"; return false; }
        }
        catch (Exception ex) { reason = $"{driveRoot} 不可访问：{ex.Message}"; return false; }

        if (!create) return true;

        try
        {
            Directory.CreateDirectory(Path.Combine(full, ".favsync"));
            var probe = Path.Combine(full, ".favsync", "write.probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) { reason = "目录不可写：" + ex.Message; return false; }
    }

    /// <summary>
    /// 启动期把总库收敛到一个真的能用的位置。<b>这是"应用一定能打开管理页"的底线</b>：
    /// 配置指向不存在的盘符 / 只读盘 / 网络盘时，不抛异常、不退出，换成本机兜底目录并留一条
    /// <see cref="BootNotice"/>，前端据此弹黄色横幅引导用户去设置页改回来。
    /// </summary>
    /// <param name="requested">配置里写的值（null 表示从没配过，直接用默认）。</param>
    /// <returns>本次实际生效的总库根。</returns>
    public string ResolveArchiveRootForBoot(string? requested)
    {
        var wanted = string.IsNullOrWhiteSpace(requested) ? DefaultArchiveRoot : requested!.Trim();
        ArchiveRootAsConfigured = wanted;

        if (IsUsable(wanted, out var reason))
        {
            ArchiveRoot = wanted;
            return EffectiveArchiveRoot;
        }

        var fallback = FallbackArchiveRoot;
        var fromDefault = string.IsNullOrWhiteSpace(requested);

        BootNotice = fromDefault
            ? $"默认总库 {wanted} 不可用（{reason}），本次改用 {fallback}。可以在「设置」里指定一个合适的目录。"
            : $"配置的总库 {wanted} 不可用（{reason}），本次临时改用 {fallback}。库地址已在「设置」里，改完即可恢复。";

        ArchiveRoot = fallback;
        return EffectiveArchiveRoot;
    }

    public string IncomingRoot => Path.Combine(EffectiveArchiveRoot, ".incoming");

    public string DatabasePath => Path.Combine(EffectiveArchiveRoot, ".favsync", "catalog.db");

    /// <summary>
    /// 生效的冷备份根（已展开绝对路径）。没配就是 null —— 与总库不同，冷备份**允许**
    /// 落在移动盘上，所以这里不做可用性降级：配错了就在设置页报错，不偷偷换目录
    /// （悄悄换目录会让"桶到底写进哪块盘"变得不可知，比报错危险得多）。
    /// </summary>
    public string? EffectiveColdRoot =>
        string.IsNullOrWhiteSpace(ColdRoot) ? null : Path.GetFullPath(ColdRoot!);

    /// <summary>
    /// 冷备份根**不能**等于或位于总库之内：扫库会递归进去把它当成条目认领，
    /// 恢复的临时目录更是直接污染账目。返回 null = 合法，否则返回拒绝原因。
    /// </summary>
    public string? ColdRootConflict(string coldRoot)
    {
        static string Norm(string p) =>
            Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var cold = Norm(coldRoot);
        var lib = Norm(EffectiveArchiveRoot);
        if (string.Equals(cold, lib, StringComparison.OrdinalIgnoreCase))
            return "冷备份目录不能等于总库目录";
        if (cold.StartsWith(lib + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return "冷备份目录不能放在总库目录里面（扫库会把它当条目认领）";
        if (lib.StartsWith(cold + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return "冷备份目录不能是总库的上级目录";
        return null;
    }

    public static FavSyncConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<FavSyncConfig>(json, JsonOptions);
                if (cfg is not null) return cfg;
            }
        }
        catch
        {
            // 配置损坏不应阻止启动：退回默认值，保存时覆盖。
        }

        return new FavSyncConfig();
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(ConfigPath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOptions));
    }
}
