using System.Formats.Tar;
using System.Runtime.CompilerServices;
using System.Text;
using FavSync.Core.Adb;

namespace FavSync.Core.Scan;

/// <summary>一条元数据（<c>entry.json</c> 或 <c>&lt;tag&gt;/index.json</c>）。</summary>
/// <param name="RelPath">相对扫描根的路径，统一用 <c>/</c> 分隔。</param>
/// <param name="Json">文件内容（已 Trim）。</param>
/// <param name="Bytes">该文件字节数（只用于进度展示）。</param>
public sealed record ScanMeta(string RelPath, string Json, long Bytes);

/// <summary>
/// 扫描数据源：把「文件清单」与「元数据 JSON」两件事抽出来，
/// 让手机（adb）和本地总库（文件系统）共用同一套解析与组装逻辑。
///
/// <para><b>为什么必须共用：</b>手机缓存目录与总库目录**同构**
/// （都是 <c>&lt;avid&gt;/c_&lt;cid&gt;/&lt;tag&gt;/</c> 且 <c>entry.json</c> 在条目根），
/// B 站那套 JSON 的字段含义也只有一份（见 <see cref="ScanItem"/>）。
/// 复制一份解析代码必然在字段语义上分叉——owner_id / 宽高漏读一处就是这种后果。</para>
///
/// <para>两个实现的行为差异只允许出现在「怎么拿到字节」这一步，不允许出现在「怎么解释字节」。</para>
/// </summary>
public interface IScanSource
{
    /// <summary>根路径的描述，只用于日志。</summary>
    string Describe { get; }

    /// <summary>
    /// 遍历全部文件（**只 stat，不 open**），回调 <c>(相对根的路径, 字节数)</c>。
    /// 路径一律 <c>/</c> 分隔，且不含前导 <c>./</c>。
    /// </summary>
    Task WalkFilesAsync(Action<string, long> onFile, CancellationToken ct = default);

    /// <summary>
    /// 只取元数据：<c>&lt;avid&gt;/c_&lt;cid&gt;/entry.json</c> 与
    /// <c>&lt;avid&gt;/c_&lt;cid&gt;/&lt;tag&gt;/index.json</c>。
    /// 路径深度不符的一律不返回（调用方据此天然忽略无关文件）。
    /// </summary>
    IAsyncEnumerable<ScanMeta> ReadMetaAsync(CancellationToken ct = default);
}

/// <summary>
/// 手机端数据源：完全复用 <see cref="Scanner"/> 原有的两段脚本。
/// <para>① <c>find -printf</c> 列清单（3.2 万个 ~1s）；② 设备端 find 生成元数据清单，
/// 单条 tar 流回传（逐行 printf 会触发 Nagle / 延迟 ACK 乒乓，实测慢 7 倍，见 Scanner 注释）。</para>
/// </summary>
public sealed class AdbScanSource : IScanSource
{
    private readonly AdbClient _adb;
    private readonly string _serial;
    private readonly string _root;

    public AdbScanSource(AdbClient adb, string serial, string phoneRoot)
    {
        _adb = adb;
        _serial = serial;
        _root = phoneRoot;
    }

    public string Describe => $"adb:{_serial}:{_root}";

    public Task WalkFilesAsync(Action<string, long> onFile, CancellationToken ct = default)
        => _adb.ShellLinesAsync(_serial, Scanner.BuildFindScript(_root), line =>
        {
            if (line.Length == 0) return;
            var tab = line.IndexOf('\t');
            if (tab <= 0) return;
            if (!long.TryParse(line.AsSpan(0, tab), out var size)) return;

            var path = Scanner.TrimDot(line[(tab + 1)..].TrimEnd('\r'));
            var slash = path.LastIndexOf('/');
            if (slash <= 0) return;                 // 根目录下的散文件不参与条目组装

            onFile(path, size);
        }, ct);

    public async IAsyncEnumerable<ScanMeta> ReadMetaAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var stream = _adb.OpenOutput(_serial, Scanner.BuildTarScript(_root));
        using var tar = new TarReader(stream);

        while (await tar.GetNextEntryAsync(cancellationToken: ct).ConfigureAwait(false) is { } entry)
        {
            if (entry.EntryType != TarEntryType.RegularFile || entry.DataStream is null) continue;

            using var reader = new StreamReader(entry.DataStream, Encoding.UTF8);
            var json = (await reader.ReadToEndAsync(ct).ConfigureAwait(false)).Trim();
            yield return new ScanMeta(Scanner.TrimDot(entry.Name), json, entry.Length);
        }

        await stream.FinishAsync(ct).ConfigureAwait(false);
    }
}
