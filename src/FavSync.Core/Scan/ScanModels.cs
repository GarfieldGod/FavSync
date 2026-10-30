namespace FavSync.Core.Scan;

public static class Roles
{
    public const string Video = "video";
    public const string Audio = "audio";
    public const string Other = "other";

    public static string FromName(string name)
    {
        var slash = name.LastIndexOf('/');
        var leaf = slash >= 0 ? name[(slash + 1)..] : name;
        return leaf switch
        {
            "video.m4s" => Video,
            "audio.m4s" => Audio,
            _ => Other,
        };
    }
}

public sealed class ScanFile
{
    /// <summary>相对条目根（<c>&lt;avid&gt;/c_&lt;cid&gt;</c>）的路径，例如 <c>116/video.m4s</c>。</summary>
    public required string Name { get; init; }
    public required string Role { get; init; }
    public required long Size { get; init; }
    public string? Md5Declared { get; init; }
}

/// <summary>手机上的一个缓存条目 = 一个 (avid, cid) 内容 + 一个 tag 档位。</summary>
public sealed class ScanItem
{
    public required string Key { get; set; }           // "<avid>_c<cid>"
    public required long Avid { get; set; }
    public required long Cid { get; set; }
    public required string Tag { get; set; }
    public required string RelDir { get; init; }       // "<avid>/c_<cid>"

    public string? Title { get; set; }
    public string? Owner { get; set; }
    /// <summary>UP 主数字 ID（B站 mid）。来自手机 entry.json 的 owner_id 或 view 的 owner.mid。</summary>
    public long? OwnerMid { get; set; }
    public string? Bvid { get; set; }
    public string? Cover { get; set; }
    public int? Page { get; set; }
    public int? Quality { get; set; }
    public int? Codecid { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public long? DurationMs { get; set; }

    public long VideoSize { get; set; }
    public string? VideoMd5 { get; set; }
    public long AudioSize { get; set; }
    public string? AudioMd5 { get; set; }

    public List<ScanFile> Files { get; } = new();

    public long TotalSize => Files.Sum(f => f.Size);

    /// <summary>
    /// 档位归一化键（DESIGN.md §3.3）：<c>q&lt;quality&gt;_c&lt;codecid&gt;</c>。
    /// codecid 缺失时退化为 <c>q&lt;quality&gt;</c> —— 宁可把两档并成一档，不可把同一档拆成两档。
    /// 与磁盘目录名（<see cref="Tag"/>）严格分离：目录名是镜像事实，这个键是判定依据。
    /// </summary>
    public string TagNorm => Quality is { } q
        ? (Codecid is { } c ? $"q{q}_c{c}" : $"q{q}")
        : $"tag:{Tag}";

    /// <summary>
    /// 内容指纹（DESIGN.md §3.3）：同一个 <c>(key, tag_norm)</c> 下区分"是不是同一份字节"。
    /// 用 video + audio 的 md5 前缀组合，而不是单个 md5 —— UP 只重传音频的情况虽然罕见，但漏了就是错判。
    /// 两者都缺（无 index.json）时才退化为 size 组合。
    /// </summary>
    public string ContentId
    {
        get
        {
            var v = VideoMd5;
            var a = AudioMd5;
            if (!string.IsNullOrEmpty(v))
            {
                var vp = v.Length >= 10 ? v[..10] : v;
                var ap = string.IsNullOrEmpty(a) ? "0" : (a.Length >= 6 ? a[..6] : a);
                return $"v{vp}a{ap}";
            }
            return $"s{VideoSize:x}a{AudioSize:x}";
        }
    }

    /// <summary>
    /// 残片判定（DESIGN.md §7.1）：只看字节数，不看 is_completed。
    /// 只有当 index.json 明确声明了大小、而实际文件与之不符时才算残片。
    /// </summary>
    public bool IsIncomplete => IncompleteIssue is not null;

    /// <summary>
    /// 残片的具体原因（null = 完整）。UI 要回答"到底少了什么东西"，
    /// 所以这里把缺哪个流、实际多少字节、声明多少字节、差多少都写清楚。
    /// 判定规则与旧 IsIncomplete 完全一致，只是把布尔值换成了人话。
    /// </summary>
    public string? IncompleteIssue
    {
        get
        {
            var v = Files.FirstOrDefault(f => Roles.FromName(f.Name) == Roles.Video);
            var a = Files.FirstOrDefault(f => Roles.FromName(f.Name) == Roles.Audio);
            if (v is null && a is null)
            {
                return Files.Count == 0
                    ? "目录里一个分片都没有（只剩元数据 JSON）"
                    : $"video/audio 两个流都缺（现有 {Files.Count} 个文件：{string.Join("、", Files.Select(f => f.Name).Take(4))}）";
            }

            var parts = new List<string>();
            if (v is null) parts.Add("缺 video 流（video.m4s 不在了）");
            else if (VideoMd5 is not null && v.Size != VideoSize)
                parts.Add(SizeIssue("video", v.Size, VideoSize));

            if (a is null) parts.Add("缺 audio 流（audio.m4s 不在了）");
            else if (AudioMd5 is not null && a.Size != AudioSize)
                parts.Add(SizeIssue("audio", a.Size, AudioSize));

            return parts.Count == 0 ? null : string.Join("；", parts);
        }
    }

    private static string SizeIssue(string what, long actual, long declared)
    {
        var diff = declared - actual;
        var tail = diff > 0 ? $"少 {FmtSize(diff)}" : $"多 {FmtSize(-diff)}";
        return $"{what} 实际 {FmtSize(actual)}，index.json 声明 {FmtSize(declared)}（{tail}）";
    }

    private static string FmtSize(long b)
    {
        if (b < 1024) return $"{b} B";
        if (b < 1024 * 1024) return $"{b / 1024.0:F0} KB";
        if (b < 1024L * 1024 * 1024) return $"{b / 1024.0 / 1024:F1} MB";
        return $"{b / 1024.0 / 1024 / 1024:F2} GB";
    }
}

public sealed class ScanResult
{
    public List<ScanItem> Items { get; } = new();
    public List<string> Warnings { get; } = new();

    /// <summary>
    /// 有 <c>entry.json</c> 但缺 <c>index.json</c> 的条目目录（相对路径）。
    ///
    /// <para>这类条目**无法认领**：档位 / 大小 / md5 的唯一事实来源就是 index.json，
    /// 没有它就算不出 <c>TagNorm</c> 与 <c>ContentId</c>，硬编一个会污染账目。
    /// 典型来源是早年「收藏夹直下」——那时 dash 信息只写进了 db，没落盘。</para>
    ///
    /// <para>它们不放进 <see cref="Warnings"/>：这不是异常，调用方应当拿账目去核对字节
    /// （见 <c>JobManager.StartLibraryScan</c> 的账实核对段），而不是当警告刷屏。</para>
    /// </summary>
    public List<string> NoIndexDirs { get; } = new();

    public int FileCount { get; set; }
    public long BytesReceived { get; set; }
}
