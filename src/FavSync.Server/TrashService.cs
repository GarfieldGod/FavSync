using System.Text.Json;
using FavSync.Core.Config;
using FavSync.Core.Store;

namespace FavSync.Server;

public sealed record TrashItem(string Key, string? Title, string? Owner, long Size,
    string? DeletedAt, int BackupCount);

/// <summary>
/// 回收站。删条目时**不真删字节**：先把 <c>&lt;avid&gt;/c_&lt;cid&gt;</c> 整棵移进
/// <c>&lt;archive&gt;/.trash/&lt;key&gt;/</c>，元信息写 meta.json（db 记录会被清掉，
/// 标题/大小/档位只能靠它保留），确认无误后再由用户清空。
///
/// 为什么用库内 .trash 而不是系统回收站：同盘 <see cref="Directory.Move"/> 是瞬时的，
/// 跨盘/走系统回收站会退化成复制+删除，慢且瞬时占双倍空间。. 开头也不会被扫描器当成视频源。
/// </summary>
public sealed class TrashService
{
    private static readonly JsonSerializerOptions J = new() { WriteIndented = false };

    private readonly FavSyncConfig _cfg;

    public TrashService(FavSyncConfig cfg) => _cfg = cfg;

    public string Root => Path.Combine(_cfg.EffectiveArchiveRoot, ".trash");

    private static string SafeName(string key) =>
        key.Replace('/', '_').Replace('\\', '_').Replace(':', '_');

    /// <summary>
    /// 把条目移进回收站并清掉数据库记录。
    /// 顺序刻意是「先搬磁盘、成功才动库」——搬失败时库里那条还在，不会出现"文件没了记录也在"的孤儿态。
    /// </summary>
    public (bool Ok, string? Error, string? Moved) MoveToTrash(string key, CatalogDatabase db)
    {
        var item = db.Get(key);
        var backups = db.ListBackups(key);
        if (item is null && backups.Count == 0) return (false, "总库里没有这个条目", null);

        var relDir = (item?.RelDir ?? backups[0].Path).Replace('\\', '/');   // "<avid>/c_<cid>"
        var size = backups.Sum(b => b.Size);
        var root = _cfg.EffectiveArchiveRoot;
        var rel = relDir.Replace('/', Path.DirectorySeparatorChar);
        var srcMain = Path.Combine(root, rel);
        var srcVer = Path.Combine(root, ".versions", rel);

        if (!Directory.Exists(srcMain) && !Directory.Exists(srcVer))
            return (false, $"目录不在预期位置（可能已被移动过）：{srcMain}", null);

        var dst = Path.Combine(Root, SafeName(key));
        if (Directory.Exists(dst)) dst += "-" + DateTime.Now.ToString("yyyyMMddHHmmss");

        try
        {
            Directory.CreateDirectory(dst);

            if (Directory.Exists(srcMain))
            {
                var d = Path.Combine(dst, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(d)!);
                Directory.Move(srcMain, d);
                PruneEmptyParents(root, srcMain);
            }
            // 额外档位（.versions/<relDir>/<tag>@<cid>）跟着一起走，不留残骸
            if (Directory.Exists(srcVer))
            {
                var d = Path.Combine(dst, ".versions", rel);
                Directory.CreateDirectory(Path.GetDirectoryName(d)!);
                Directory.Move(srcVer, d);
                PruneEmptyParents(Path.Combine(root, ".versions"), srcVer);
            }

            var meta = new
            {
                key,
                title = item?.Title,
                owner = item?.Owner,
                avid = item?.Avid,
                cid = item?.Cid,
                quality = backups.Count > 0 ? backups.Max(b => b.Quality) : item?.BestQuality,
                size,
                backupCount = backups.Count,
                relDir,
                deletedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            };
            File.WriteAllText(Path.Combine(dst, "meta.json"), JsonSerializer.Serialize(meta, J));
        }
        catch (Exception ex)
        {
            return (false, $"移入回收站失败：{ex.Message}", null);
        }

        db.DeleteItem(key);   // 磁盘已安全落位，才动数据库
        return (true, null, dst);
    }

    public IReadOnlyList<TrashItem> List()
    {
        var root = Root;
        if (!Directory.Exists(root)) return Array.Empty<TrashItem>();

        var list = new List<TrashItem>();
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            string? title = null, owner = null, deletedAt = null;
            long size = 0;
            var bc = 0;
            var meta = Path.Combine(dir, "meta.json");
            if (File.Exists(meta))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(meta));
                    var o = doc.RootElement;
                    if (o.TryGetProperty("title", out var t)) title = t.GetString();
                    if (o.TryGetProperty("owner", out var w)) owner = w.GetString();
                    if (o.TryGetProperty("deletedAt", out var d)) deletedAt = d.GetString();
                    if (o.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv)) size = sv;
                    if (o.TryGetProperty("backupCount", out var b) && b.TryGetInt32(out var bv)) bc = bv;
                }
                catch { /* meta 坏了就按目录名显示，不影响清空 */ }
            }
            if (size == 0) size = DirSize(dir);
            list.Add(new TrashItem(Path.GetFileName(dir), title, owner, size, deletedAt, bc));
        }
        return list.OrderByDescending(x => x.DeletedAt ?? string.Empty).ToList();
    }

    public (int Count, long Bytes) Empty()
    {
        var root = Root;
        if (!Directory.Exists(root)) return (0, 0);

        var n = 0;
        long bytes = 0;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            try
            {
                bytes += DirSize(dir);
                Directory.Delete(dir, true);
                n++;
            }
            catch { /* 被占用/权限：跳过，下次再清 */ }
        }
        return (n, bytes);
    }

    private static long DirSize(string dir)
    {
        try
        {
            long sum = 0;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { sum += new FileInfo(f).Length; } catch { }
            }
            return sum;
        }
        catch { return 0; }
    }

    /// <summary>搬走子目录后，把变空的 <c>&lt;avid&gt;</c> 父目录也收掉（不越过 stopAt）。</summary>
    private static void PruneEmptyParents(string stopAt, string movedPath)
    {
        try
        {
            var stop = Path.GetFullPath(stopAt).TrimEnd(Path.DirectorySeparatorChar);
            var dir = Path.GetDirectoryName(movedPath.TrimEnd(Path.DirectorySeparatorChar));
            while (dir is not null &&
                   dir.Length > stop.Length &&
                   dir.StartsWith(stop, StringComparison.OrdinalIgnoreCase) &&
                   Directory.Exists(dir) &&
                   !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch { }
    }
}
