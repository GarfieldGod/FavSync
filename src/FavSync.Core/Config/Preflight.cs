namespace FavSync.Core.Config;

public sealed record PreflightCheck(string Name, bool Ok, string Detail);

/// <summary>
/// 启动预检（DESIGN.md §6）。任一不过就必须拒绝写入，而不是"先跑起来再说"。
/// 这里的每一条都对应一种会造成静默数据损坏的部署方式。
/// </summary>
public static class Preflight
{
    public static IReadOnlyList<PreflightCheck> Run(FavSyncConfig cfg)
    {
        var checks = new List<PreflightCheck>();
        var root = cfg.EffectiveArchiveRoot;

        // 1) 必须是本地固定盘。SQLite 的文件锁在 SMB/UNC 上不可靠，会静默损坏。
        var (driveOk, driveDetail) = CheckLocalFixed(root);
        checks.Add(new PreflightCheck("总库为本地固定盘", driveOk, driveDetail));

        // 2) .incoming 与 archive 必须同卷，否则 rename 退化为"复制+删除"，失去原子性。
        var incoming = cfg.IncomingRoot;
        var sameVolume = string.Equals(
            Path.GetPathRoot(root), Path.GetPathRoot(incoming), StringComparison.OrdinalIgnoreCase);
        checks.Add(new PreflightCheck(
            "事务区与总库同卷",
            sameVolume,
            sameVolume ? $"{incoming}" : $".incoming 在 {Path.GetPathRoot(incoming)}，总库在 {Path.GetPathRoot(root)}"));

        // 3) 可写
        try
        {
            Directory.CreateDirectory(root);
            var probe = Path.Combine(root, ".favsync", "write.probe");
            Directory.CreateDirectory(Path.GetDirectoryName(probe)!);
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            checks.Add(new PreflightCheck("总库可写", true, root));
        }
        catch (Exception ex)
        {
            checks.Add(new PreflightCheck("总库可写", false, ex.Message));
        }

        return checks;
    }

    private static (bool, string) CheckLocalFixed(string path)
    {
        try
        {
            if (path.StartsWith(@"\\", StringComparison.Ordinal))
                return (false, "UNC 网络路径不允许作为总库（SQLite 文件锁不可靠）");

            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root))
                return (false, $"无法解析卷：{path}");

            var di = new DriveInfo(root);
            if (di.DriveType != DriveType.Fixed)
                return (false, $"{root} 的驱动器类型是 {di.DriveType}，要求 Fixed");

            return (true, $"{di.Name.TrimEnd('\\')} 可用 {di.AvailableFreeSpace / 1024 / 1024 / 1024} GB");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>同步前的空间预检：需要 X，剩余 Y。</summary>
    public static (bool Ok, long Need, long Free) CheckSpace(FavSyncConfig cfg, long needBytes)
    {
        try
        {
            var di = new DriveInfo(Path.GetPathRoot(cfg.EffectiveArchiveRoot)!);
            var need = (long)(needBytes * 1.05);
            return (di.AvailableFreeSpace >= need, need, di.AvailableFreeSpace);
        }
        catch
        {
            return (true, needBytes, long.MaxValue);
        }
    }
}
