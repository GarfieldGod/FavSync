using System.Runtime.CompilerServices;

namespace FavSync.Core.Scan;

/// <summary>
/// 本地总库数据源。总库目录与手机缓存目录**同构**
/// （<c>&lt;avid&gt;/c_&lt;cid&gt;/&lt;tag&gt;/video.m4s</c>，<c>entry.json</c> 在条目根），
/// 所以扫库能复用 <see cref="Scanner"/> 的全部解析逻辑，只是把「怎么拿到字节」换成读磁盘。
///
/// <para><b>用途：</b>把外部拷进总库的条目认领入库（中继回传、手工搬运、换机迁移），
/// 以及体检「磁盘上有但账目不知道 / 账目说有时磁盘没有」。</para>
///
/// <para><b>与手机源的关键区别：</b>这里看到的是**已经落盘的字节**，不是手机现状。
/// 所以调用方绝对不能用 <c>MergeScan</c>（那会覆盖 <c>phone_*</c> 并把没扫到的条目置
/// <c>on_phone=0</c>），只能走 <c>RegisterBackup</c>。</para>
/// </summary>
public sealed class LocalLibrarySource : IScanSource
{
    /// <summary>
    /// 总库里**不是条目**的目录。以 <c>.</c> 开头的全部跳过（<c>.favsync</c> 账目与临时区、
    /// <c>.incoming</c> 暂存、<c>.trash</c> 回收站），外加封面缓存 <c>covers</c>。
    ///
    /// <para>跳过 <c>.trash</c> 尤其重要：它的结构是 <c>.trash/&lt;key&gt;/...</c>，
    /// 里面同样有 <c>entry.json</c> / <c>index.json</c>，一旦被当成条目就会把已删的东西重新认领回来。</para>
    /// </summary>
    private static readonly string[] SkipDirNames = ["covers"];

    private readonly string _root;

    public LocalLibrarySource(string archiveRoot) => _root = Path.GetFullPath(archiveRoot);

    public string Describe => $"library:{_root}";

    public Task WalkFilesAsync(Action<string, long> onFile, CancellationToken ct = default)
        => Task.Run(() =>
        {
            foreach (var (abs, rel) in Dirs(ct))
            {
                DirectoryInfo di;
                try { di = new DirectoryInfo(abs); } catch { continue; }

                IEnumerable<FileSystemInfo> entries;
                try { entries = di.EnumerateFileSystemInfos(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

                foreach (var e in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (e is FileInfo f)
                    {
                        // EnumerateFileSystemInfos 给出的 FileInfo 已带大小，不会额外 stat。
                        // 路径必须与 adb 源同形（相对根、'/' 分隔）——Scanner 后面按
                        // "<relDir>/" 前缀切出条目的文件集，少一个分隔符就会把文件名记成
                        // "64video.m4s"，role 也会退化成 other。
                        onFile(rel.Length == 0 ? f.Name : rel + "/" + f.Name, f.Length);
                    }
                }
            }
        }, ct);

    public async IAsyncEnumerable<ScanMeta> ReadMetaAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        // 只认两种形状：<avid>/c_<cid>/entry.json（2 段目录）与
        // <avid>/c_<cid>/<tag>/index.json（3 段目录）。更深的路径（历史上附加版本那一套是 5 段）
        // 一律不返回 —— 形状校验比"按名字排除"更稳，不用维护排除表。
        foreach (var (abs, rel) in Dirs(ct))
        {
            var depth = rel.Length == 0 ? 0 : rel.Count(c => c == '/') + 1;
            if (depth is not (2 or 3)) continue;
            if (!IsContentPath(rel)) continue;

            var name = depth == 2 ? "entry.json" : "index.json";
            var file = Path.Combine(abs, name);
            if (!File.Exists(file)) continue;

            string json;
            long bytes;
            try
            {
                json = (await File.ReadAllTextAsync(file, ct).ConfigureAwait(false)).Trim();
                bytes = new FileInfo(file).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            yield return new ScanMeta(rel + "/" + name, json, bytes);
        }
    }

    /// <summary>深度优先枚举所有「非条目目录」。<paramref name="rel"/> 为相对根的 <c>/</c> 分隔路径，根为空串。</summary>
    private IEnumerable<(string Abs, string Rel)> Dirs(CancellationToken ct)
    {
        var stack = new Stack<(string Abs, string Rel)>();
        stack.Push((_root, ""));

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (abs, rel) = stack.Pop();
            yield return (abs, rel);

            IEnumerable<DirectoryInfo> subs;
            try { subs = new DirectoryInfo(abs).EnumerateDirectories(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (var d in subs)
            {
                if (d.Name.StartsWith('.')) continue;
                if (SkipDirNames.Contains(d.Name, StringComparer.OrdinalIgnoreCase)) continue;
                stack.Push((d.FullName, rel.Length == 0 ? d.Name : rel + "/" + d.Name));
            }
        }
    }

    /// <summary>
    /// <c>&lt;avid&gt;/c_&lt;cid&gt;[/&lt;tag&gt;]</c> 形状校验。第一段必须是纯数字、第二段必须以
    /// <c>c_</c> 开头——这一条同时挡住了回收站、封面缓存这类误入目录。
    /// </summary>
    private static bool IsContentPath(string rel)
    {
        var parts = rel.Split('/');
        if (parts.Length < 2) return false;
        if (!long.TryParse(parts[0], out var avid) || avid <= 0) return false;
        return parts[1].StartsWith("c_", StringComparison.Ordinal) && parts[1].Length > 2;
    }
}
