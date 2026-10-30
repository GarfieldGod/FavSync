using System.Text;
using System.Text.Json;
using FavSync.Core.Adb;

namespace FavSync.Core.Scan;

public sealed record ScanProgress(int Items, int Expected, long Bytes);

/// <summary>
/// 扫描器：两阶段取回全量元数据（DESIGN.md §4.2）。
///
///   ① 文本：find -printf 列出全部文件（只 stat 不 open），3.2 万个 ~1s；
///   ② tar：设备端 find 生成元数据清单（entry.json + index.json）→ 单条 tar 流回传，
///      PC 端 System.Formats.Tar 流式解析。
///
/// 为什么不用逐行读（实测，100 个 json）：
///   设备本地读        4.4 ms/文件（读盘便宜）
///   tar 单流回传      7   ms/文件
///   逐行 printf 回传  51  ms/文件 ← 每文件一次小写入，触发 Nagle/延迟 ACK 乒乓
/// 瓶颈既不是 FUSE open 也不是带宽，是**写入模式**。全量 10850 个 json：
/// 逐行 ~9.5 min → tar ~1.5 min。所以传输一律走二进制单流（与 ItemCopier 同理）。
/// </summary>
public sealed class Scanner
{
    private readonly AdbClient _adb;

    public Scanner(AdbClient adb) => _adb = adb;

    /// <summary>
    /// 只用双引号（外层包装是 sh -c '...'，见 ShellQuote）。
    /// 只遍历 */c_*，所以 s_ 前缀（番剧）目录天然被跳过。
    /// </summary>
    public static string BuildFindScript(string phoneRoot) => $$"""
        cd "{{phoneRoot}}" 2>/dev/null || { echo "@@ERR	root not found"; exit 3; }
        find . -type f -printf "%s\t%p\n"
        """;

    /// <summary>
    /// 元数据清单 + tar。清单走 find -path 而不是 shell 循环 glob：
    /// find 一次遍历 ~1s，循环里每个 [ -f ] 都是 FUSE stat（5427 个目录不划算）。
    /// 解析侧按键 ( avid, c_cid, 文件名 ) 分派，深度不符的路径天然被忽略。
    /// </summary>
    public static string BuildTarScript(string phoneRoot) => $$"""
        cd "{{phoneRoot}}" 2>/dev/null || exit 3
        find . \( -path "*/c_*/entry.json" -o -path "*/c_*/*/index.json" \) > /data/local/tmp/favsync_meta
        tar cf - -T /data/local/tmp/favsync_meta
        rc=$?
        rm -f /data/local/tmp/favsync_meta
        exit $rc
        """;

    public Task<ScanResult> ScanAsync(
        string serial,
        string phoneRoot,
        Action<ScanProgress>? progress = null,
        CancellationToken ct = default)
        => ScanSourceAsync(new AdbScanSource(_adb, serial, phoneRoot), progress, ct);

    /// <summary>
    /// 数据源无关的扫描主体：手机（adb）与本地总库共用这一段。
    /// 「怎么拿到字节」交给 <see cref="IScanSource"/>，「怎么解释字节」只在这里写一次。
    /// </summary>
    public async Task<ScanResult> ScanSourceAsync(
        IScanSource source,
        Action<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        var result = new ScanResult();
        var allFiles = new List<(string Path, long Size)>();
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        var indexes = new List<(string RelDir, string Tag, string Json)>();

        var expected = 0;
        long bytes = 0;

        // ---- 阶段 ①：文件清单（只 stat，不开文件）----
        await source.WalkFilesAsync((path, size) =>
        {
            var slash = path.LastIndexOf('/');
            if (slash <= 0) return;                 // 数据源已过滤，这里再挡一次
            var dir = path[..slash];
            var name = path[(slash + 1)..];

            allFiles.Add((path, size));
            result.FileCount++;

            if (name == "index.json" && dir.Split('/').Length == 3) expected++;
        }, ct).ConfigureAwait(false);

        if (result.FileCount == 0)
            throw new InvalidOperationException($"扫描源为空或不可访问：{source.Describe}");

        // ---- 阶段 ②：元数据（手机走 tar 单流，本地库直接读文件）----
        await foreach (var meta in source.ReadMetaAsync(ct).ConfigureAwait(false))
        {
            var parts = meta.RelPath.Split('/');
            bytes += meta.Bytes;

            if (parts.Length == 3 && parts[2] == "entry.json")
            {
                entries[$"{parts[0]}/{parts[1]}"] = meta.Json;
            }
            else if (parts.Length == 4 && parts[3] == "index.json")
            {
                if (meta.Json.Length == 0) continue;
                indexes.Add(($"{parts[0]}/{parts[1]}", parts[2], meta.Json));
                progress?.Invoke(new ScanProgress(indexes.Count, expected, bytes));
            }
        }

        // ---- 组装条目 ----
        var byRel = new Dictionary<string, ScanItem>(StringComparer.Ordinal);
        foreach (var (relDir, tag, json) in indexes)
        {
            var item = new ScanItem
            {
                Key = MakeKey(relDir),
                Avid = AvidFromRel(relDir),
                Cid = CidFromRel(relDir),
                Tag = tag,
                RelDir = relDir,
            };

            ApplyEntryJson(item, entries.GetValueOrDefault(relDir));
            ApplyIndexJson(item, json);

            // 镜像单位是 <avid>/c_<cid>/ 整目录：entry.json / danmaku.xml / cover.jpg 都在这一层，
            // 只搬 <tag>/ 会漏掉它们（实测踩过）。
            var prefix = relDir + "/";
            foreach (var (path, size) in allFiles)
            {
                if (!path.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var rel2 = path[prefix.Length..];
                var role = Roles.FromName(rel2);
                item.Files.Add(new ScanFile
                {
                    Name = rel2,
                    Role = role,
                    Size = size,
                    Md5Declared = role switch
                    {
                        Roles.Video => item.VideoMd5,
                        Roles.Audio => item.AudioMd5,
                        _ => null,
                    },
                });
            }

            if (item.Files.Count == 0)
            {
                result.Warnings.Add($"{relDir}/{tag}：index.json 存在但目录内无文件，已跳过");
                continue;
            }

            if (byRel.TryGetValue(relDir, out var first))
            {
                result.Warnings.Add($"{relDir}：存在多个 tag 目录，只取 {first.Tag}，忽略 {tag}");
                continue;
            }

            byRel[relDir] = item;
        }

        foreach (var rel in entries.Keys)
        {
            if (!byRel.ContainsKey(rel))
                result.NoIndexDirs.Add(rel);     // 不是异常：交给调用方用账目核对字节
        }

        result.Items.AddRange(byRel.Values.OrderBy(i => i.Avid).ThenBy(i => i.Cid));
        result.BytesReceived = bytes;
        return result;
    }

    /// <summary>去掉 <c>./</c> 前缀与前导 <c>/</c>。两个数据源共用同一套路径归一化。</summary>
    internal static string TrimDot(string path)
    {
        var p = path.Trim();
        while (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
        return p.TrimStart('/');
    }

    /// <summary>内容键：<c>&lt;avid&gt;_c&lt;cid&gt;</c>。只从路径算，json 里的值另做校验。</summary>
    public static string MakeKey(string relDir)
    {
        var parts = relDir.Split('/');
        if (parts.Length < 2) throw new ArgumentException($"非法缓存路径：{relDir}");
        var avid = parts[^2];
        var cid = parts[^1];
        if (cid.StartsWith("c_", StringComparison.Ordinal)) cid = cid[2..];
        return $"{avid}_c{cid}";
    }

    private static long AvidFromRel(string relDir)
    {
        var parts = relDir.Split('/');
        return long.TryParse(parts[^2], out var v) ? v : 0;
    }

    private static long CidFromRel(string relDir)
    {
        var parts = relDir.Split('/');
        var cid = parts[^1];
        if (cid.StartsWith("c_", StringComparison.Ordinal)) cid = cid[2..];
        return long.TryParse(cid, out var v) ? v : 0;
    }

    private static void ApplyEntryJson(ScanItem item, string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            using var doc = ParseEntry(json);
            if (doc is null) return;
            var r = doc.RootElement;

            // 优先用 json 里的 avid / cid：实测明文存在，比解析目录名可靠。
            if (TryGetLong(r, "avid", out var avid) && avid > 0) item.Avid = avid;
            if (r.TryGetProperty("page_data", out var page) && page.ValueKind == JsonValueKind.Object)
            {
                if (TryGetLong(page, "cid", out var cid) && cid > 0) item.Cid = cid;
                if (TryGetLong(page, "page", out var pageNo)) item.Page = (int)pageNo;
                item.Title = GetString(page, "part");
            }

            item.Title ??= GetString(r, "title");
            item.Owner = GetString(r, "owner_name");
            // UP 主数字 ID。手机缓存的 entry.json 本来就有，扫一次顺手存库——
            // 存量补 owner_id 时能省掉一整轮 view 请求（680 次 ≈ 40 分钟）。
            if (TryGetLong(r, "owner_id", out var omid) && omid > 0) item.OwnerMid = omid;
            item.Bvid = GetString(r, "bvid");
            item.Cover = GetString(r, "cover");
            if (TryGetLong(r, "total_time_milli", out var dur)) item.DurationMs = dur;
            // 分辨率：手机 entry.json 的 page_data 带，之前没读 → 库里 0×0
            if (page.ValueKind == JsonValueKind.Object)
            {
                if (TryGetLong(page, "width", out var pw) && pw > 0) item.Width = (int)pw;
                if (TryGetLong(page, "height", out var ph) && ph > 0) item.Height = (int)ph;
            }

            item.Key = $"{item.Avid}_c{item.Cid}";
        }
        catch
        {
            // 字段版本差异不应阻断备份（DESIGN.md §3.2 整目录搬原则）：解析失败只影响展示。
        }
    }

    /// <summary>
    /// 解析 entry.json。B 站 App 偶尔在末尾多写一个 '}'（实测 1/1284，手机上的原文与总库字节完全相同，
    /// 所以不是我们写坏的）——严格解析会整条丢掉 title/owner。失败时从尾部去掉 1~4 个字符重试。
    /// </summary>
    private static JsonDocument? ParseEntry(string json)
    {
        try { return JsonDocument.Parse(json); }
        catch { }

        var s = json.TrimEnd();
        for (var i = 1; i <= 4 && s.Length > i + 1; i++)
        {
            var t = s[..^i].TrimEnd();
            if (t.Length == 0 || t[^1] != '}') continue;   // 只试"去过尾巴后仍以 } 收尾"的形态
            try { return JsonDocument.Parse(t); }
            catch { }
        }
        return null;
    }

    private static void ApplyIndexJson(ScanItem item, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;

        if (r.TryGetProperty("video", out var videos) && videos.ValueKind == JsonValueKind.Array && videos.GetArrayLength() > 0)
        {
            var v = videos[0];
            if (TryGetLong(v, "size", out var size)) item.VideoSize = size;
            item.VideoMd5 = GetString(v, "md5");
            if (TryGetLong(v, "id", out var q)) item.Quality = (int)q;
            if (TryGetLong(v, "codecid", out var cc)) item.Codecid = (int)cc;
            if (TryGetLong(v, "width", out var w)) item.Width = (int)w;
            if (TryGetLong(v, "height", out var h)) item.Height = (int)h;
        }

        if (r.TryGetProperty("audio", out var audios) && audios.ValueKind == JsonValueKind.Array && audios.GetArrayLength() > 0)
        {
            var a = audios[0];
            if (TryGetLong(a, "size", out var size)) item.AudioSize = size;
            item.AudioMd5 = GetString(a, "md5");
        }
    }

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool TryGetLong(JsonElement e, string name, out long value)
    {
        value = 0;
        if (!e.TryGetProperty(name, out var v)) return false;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt64(out value),
            JsonValueKind.String => long.TryParse(v.GetString(), out value),
            _ => false,
        };
    }
}
