using System.Formats.Tar;
using System.IO.Pipelines;
using System.Security.Cryptography;
using FavSync.Core.Adb;
using FavSync.Core.Scan;

namespace FavSync.Core.Sync;

public sealed record CopyOutcome(
    bool Ok,
    string Phase,                 // copied | verified | committed | skipped | failed
    string? Error = null,
    int FileCount = 0,
    long Bytes = 0,
    IReadOnlyList<string>? Notes = null);

/// <summary>
/// 单个条目的搬运：手机目录 --tar--> .incoming --> 校验 --> 原子 rename 进总库。
///
/// 用 tar 而不是逐文件 adb pull：单文件 pull 实测 935 ms 往返，
/// 一个条目 3 个文件就是 2.8 s 纯等待；tar 只有一次往返。
/// </summary>
public sealed class ItemCopier
{
    private readonly AdbClient _adb;

    public ItemCopier(AdbClient adb) => _adb = adb;

    /// <summary>
    /// 整搬 <c>&lt;avid&gt;/c_&lt;cid&gt;/</c>，不是只搬 <c>&lt;tag&gt;/</c>：
    /// entry.json、danmaku.xml、cover.jpg 都在上一级，只搬 tag 会漏。
    /// </summary>
    public static string TarScript(string phoneRoot, string relDir) =>
        $"cd \"{phoneRoot}/{relDir}\" || exit 4; tar -cf - .";

    /// <summary>
    /// 只搬元数据小文件（entry.json / danmaku.xml …），排除 m4s 与 index.json。
    ///
    /// 用于「同画质同编码但库里是网页版字节」的场景（drift=webmux）：
    /// 视频/音频没必要再搬一遍（内容一致，只是封装不同），但**元数据必须换成正版**——
    /// 我们自己生成的 entry.json 缺 title / owner_id / 宽高，外部播放器扫到会退化。
    ///
    /// 排除 index.json 是硬要求：它声明的 md5/size 属于手机字节，拷进装网页版 m4s 的目录后
    /// 外部播放器的 VerifyFile 会判「大小不符 → 损坏」，反而把条目毁掉。
    /// （外部播放器对**缺** index.json 是宽容的，只记一条 problem，条目照常入库。）
    /// </summary>
    public static string TarScriptMetaOnly(string phoneRoot, string relDir) =>
        $"cd \"{phoneRoot}/{relDir}\" || exit 4; tar -cf - --exclude=*.m4s --exclude=index.json .";

    public async Task<CopyOutcome> CopyAsync(
        string serial,
        string phoneRoot,
        string relDir,
        string destDir,
        IReadOnlyList<ScanFile> expected,
        string? videoMd5,
        string? audioMd5,
        IProgress<long>? bytesProgress,
        CancellationToken ct = default,
        bool metaOnly = false)
    {
        var notes = new List<string>();
        Directory.CreateDirectory(destDir);

        var pipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: 4L << 20,
            resumeWriterThreshold: 2L << 20,
            useSynchronizationContext: false));

        var producer = Task.Run(async () =>
        {
            try
            {
                var script = metaOnly ? TarScriptMetaOnly(phoneRoot, relDir) : TarScript(phoneRoot, relDir);
            await _adb.PipeToAsync(serial, script, pipe.Writer.AsStream(), ct);
            }
            finally
            {
                await pipe.Writer.CompleteAsync();
            }
        }, ct);

        var extracted = new Dictionary<string, (long Size, string Md5)>(StringComparer.Ordinal);
        var destFull = Path.GetFullPath(destDir);
        long totalBytes = 0;

        try
        {
            await using var readStream = pipe.Reader.AsStream();
            await using var tar = new TarReader(readStream, leaveOpen: false);

            while (await tar.GetNextEntryAsync(cancellationToken: ct).ConfigureAwait(false) is { } entry)
            {
                var name = NormalizeEntryName(entry.Name);
                if (name.Length == 0) continue;

                var full = Path.GetFullPath(Path.Combine(destFull, name));
                if (!full.StartsWith(destFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"tar 条目越界：{entry.Name}");

                // 判目录只能看 EntryType。.NET 的 TarReader 对「0 字节普通文件」返回的
                // DataStream 也是 null（实测：手机上的空 danmaku.xml），以前拿
                // `DataStream is null` 当目录判据，会给 danmaku.xml 建一个空目录、
                // 不登记进 extracted，随后 L1 校验报「缺 [danmaku.xml]」整条失败。
                if (entry.EntryType == TarEntryType.Directory)
                {
                    Directory.CreateDirectory(full);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                await using var fs = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
                using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                var buffer = new byte[1 << 16];
                long size = 0;
                if (entry.DataStream is { } data)
                {
                    int n;
                    while ((n = await data.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        await fs.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                        md5.AppendData(buffer, 0, n);
                        size += n;
                        totalBytes += n;
                        bytesProgress?.Report(n);
                    }
                }
                await fs.FlushAsync(ct).ConfigureAwait(false);
                extracted[name] = (size, Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant());
            }

            await producer.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            TryDeleteDirectory(destDir);
            try { await producer.ConfigureAwait(false); } catch { }
            return new CopyOutcome(false, "failed", $"搬运失败：{ex.Message}");
        }

        // ---- L1 结构校验：文件集 + 每个文件 size ----
        var expectedNames = expected.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        var actualNames = extracted.Keys.ToHashSet(StringComparer.Ordinal);

        var missing = expectedNames.Except(actualNames).ToList();
        var extra = actualNames.Except(expectedNames).ToList();
        if (missing.Count > 0 || extra.Count > 0)
        {
            TryDeleteDirectory(destDir);
            return new CopyOutcome(false, "failed",
                $"文件集不一致：缺 [{string.Join(",", missing)}] 多 [{string.Join(",", extra)}]");
        }

        foreach (var f in expected)
        {
            if (extracted[f.Name].Size != f.Size)
            {
                TryDeleteDirectory(destDir);
                return new CopyOutcome(false, "failed",
                    $"{f.Name} 大小不符：期望 {f.Size}，实际 {extracted[f.Name].Size}");
            }
        }

        // ---- 内容校验：md5 必须等于 index.json 的声明值 ----
        // 这一步在搬运过程中顺带完成，成本为零，却能把"落盘损坏"和"内容被换"一起挡住。
        var videoEntry = extracted.FirstOrDefault(kv => Roles.FromName(kv.Key) == Roles.Video);
        var audioEntry = extracted.FirstOrDefault(kv => Roles.FromName(kv.Key) == Roles.Audio);

        if (videoMd5 is not null && videoEntry.Key is not null && videoEntry.Value.Md5 != videoMd5)
        {
            TryDeleteDirectory(destDir);
            return new CopyOutcome(false, "failed",
                $"{videoEntry.Key} md5 不符：期望 {videoMd5}，实际 {videoEntry.Value.Md5}");
        }
        if (audioMd5 is not null && audioEntry.Key is not null && audioEntry.Value.Md5 != audioMd5)
        {
            TryDeleteDirectory(destDir);
            return new CopyOutcome(false, "failed",
                $"{audioEntry.Key} md5 不符：期望 {audioMd5}，实际 {audioEntry.Value.Md5}");
        }

        notes.Add($"L1 通过：{extracted.Count} 个文件 / {totalBytes / 1024 / 1024} MB");
        return new CopyOutcome(true, "verified", null, extracted.Count, totalBytes, notes);
    }

    /// <summary>原子提交：同一卷上的 Directory.Move 是原子操作。</summary>
    public static CopyOutcome Commit(string stagingDir, string targetDir)
    {
        try
        {
            if (Directory.Exists(targetDir))
            {
                TryDeleteDirectory(stagingDir);
                return new CopyOutcome(true, "skipped", "目标已存在，未覆盖（只增不改）");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(targetDir)!);
            Directory.Move(stagingDir, targetDir);
            return new CopyOutcome(true, "committed");
        }
        catch (Exception ex)
        {
            return new CopyOutcome(false, "failed", $"提交失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 元数据补齐提交：把 staging 里的小文件**覆盖**进已存在的主副本目录。
    ///
    /// 与 <see cref="Commit"/> 的区别：Commit 是"整目录 Move、目标存在就整体跳过"，
    /// 这里目标**必须**已存在，且只动元数据，m4s 一个字节都不碰。
    /// 覆盖是用户明确授权的：同档同编码时视频音频无需重搬，但元数据要换成正版。
    /// </summary>
    public static CopyOutcome CommitTopUp(string stagingDir, string targetDir, IEnumerable<string> relNames)
    {
        try
        {
            if (!Directory.Exists(targetDir))
                return new CopyOutcome(false, "failed", $"主副本目录不存在，无法补齐：{targetDir}");

            var n = 0;
            var touched = new List<string>();
            foreach (var rel in relNames)
            {
                var src = Path.Combine(stagingDir, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(src)) continue;
                var dst = Path.Combine(targetDir, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst, overwrite: true);
                touched.Add(rel);
                n++;
            }
            TryDeleteDirectory(stagingDir);
            return new CopyOutcome(true, "topup", null, n, 0,
                new[] { $"补齐元数据：{string.Join(", ", touched)}" });
        }
        catch (Exception ex)
        {
            return new CopyOutcome(false, "failed", $"补齐失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 主副本的 entry.json 是不是手机原版（33 字段）。
    /// 判据是 <c>cache_version_code</c> —— 我们自己合成的那份没有这个字段。
    /// 用来判断"元数据还需不需要补齐"，避免每轮同步都重复搬一遍。
    /// </summary>
    public static bool IsPhoneGradeEntry(string itemDir)
    {
        try
        {
            var p = Path.Combine(itemDir, "entry.json");
            if (!File.Exists(p)) return false;
            var text = File.ReadAllText(p);
            return text.Contains("cache_version_code", StringComparison.OrdinalIgnoreCase)
                || text.Contains("is_completed", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string NormalizeEntryName(string name)
    {
        var n = name.Replace('\\', '/').Trim();
        while (n.StartsWith("./", StringComparison.Ordinal)) n = n[2..];
        if (n is "." or "") return string.Empty;
        return n.TrimStart('/');
    }

    public static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch { }
    }
}
