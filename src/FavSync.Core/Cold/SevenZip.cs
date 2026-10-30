using System.Diagnostics;
using System.Text;

namespace FavSync.Core.Cold;

/// <summary>
/// 7-Zip 命令行的一层薄封装。为什么用外部 7z 而不是自己写加密容器：
/// .NET 没有内置的"单文件 + 密码 + 加密文件名"归档格式；自研一套 AES 容器一旦有 bug，
/// 坏的是 234 GiB 的唯一副本，而 7z 的 <c>-mhe=on</c> 连包内文件名都加密（错密码连列都列不出来）。
/// 策略与 adb 完全一致：**程序自带 + 设置项可覆盖 + 留空自动查找**。
/// </summary>
public static class SevenZip
{
    /// <summary>
    /// 解析顺序：配置 → 环境变量 → <b>程序自带 tools</b> → PATH → 安装目录。
    /// 自带的排在 PATH 之前：仓库里那台机器 PATH 上就藏着一个 2009 年的 9.10 版，
    /// 让它抢先生效会让"同一个包在不同机器上行为不同"。
    /// </summary>
    public static string? Resolve(string? configured)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured)) candidates.Add(configured.Trim());

        var env = Environment.GetEnvironmentVariable("FAVSYNC_7Z");
        if (!string.IsNullOrWhiteSpace(env)) candidates.Add(env.Trim());

        // 旧布局（tools\ 根下的 7za.exe）留着兜底：早期解压包就地换 exe 时不会突然找不到。
        var baseDir = AppContext.BaseDirectory;
        foreach (var rel in new[] { "tools\\7z\\7za.exe", "tools\\7z\\7z.exe", "tools\\7za.exe", "tools\\7z.exe", "7za.exe", "7z.exe" })
            candidates.Add(Path.Combine(baseDir, rel));

        foreach (var name in new[] { "7za.exe", "7z.exe", "7zr.exe" })
        {
            var onPath = FindOnPath(name);
            if (onPath is not null) candidates.Add(onPath);
        }

        foreach (var pf in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            if (!string.IsNullOrWhiteSpace(pf)) candidates.Add(Path.Combine(pf, "7-Zip", "7z.exe"));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindOnPath(string name)
    {
        try
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try
                {
                    var full = Path.Combine(dir.Trim(), name);
                    if (File.Exists(full)) return full;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    /// <summary>7z 的参数里绝不能出现密码——它会被写进日志/异常信息。调用方负责只传参不落盘。</summary>
    public sealed record Result(int ExitCode, string Output)
    {
        public bool Ok => ExitCode == 0;
    }

    /// <summary>
    /// 跑一次 7z。<paramref name="onPercent"/> 拿的是 7z 自己报的百分比（0~100），
    /// 用来做真实进度而不是"打完了才跳一格"。
    /// </summary>
    public static async Task<Result> RunAsync(
        string exe, IEnumerable<string> args, string? workingDir,
        Action<int>? onPercent, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (!string.IsNullOrEmpty(workingDir)) psi.WorkingDirectory = workingDir;
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi };
        var sb = new StringBuilder();
        var gate = new object();

        try { p.Start(); }
        catch (Exception ex)
        {
            return new Result(-1, "无法启动 7z：" + ex.Message);
        }

        // 关掉 stdin：错密码时 7z 会等输入，不关就是永久挂起
        try { p.StandardInput.Close(); } catch { }

        var stdout = ReadAsync(p.StandardOutput, sb, gate, onPercent);
        var stderr = ReadAsync(p.StandardError, sb, gate, null);

        using var reg = ct.Register(() =>
        {
            try { if (!p.HasExited) p.Kill(true); } catch { }
        });

        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        await p.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        string text;
        lock (gate) text = sb.ToString();
        return new Result(p.ExitCode, text);
    }

    /// <summary>
    /// 逐字符读：7z 的进度是用 <c>\r</c> 原地刷新的，按行读会一直阻塞到结束。
    /// </summary>
    private static async Task ReadAsync(StreamReader r, StringBuilder sb, object gate,
        Action<int>? onPercent)
    {
        var buf = new char[512];
        var line = new StringBuilder();
        while (true)
        {
            int n;
            try { n = await r.ReadAsync(buf.AsMemory()).ConfigureAwait(false); }
            catch { return; }
            if (n <= 0) break;

            for (var i = 0; i < n; i++)
            {
                var c = buf[i];
                if (c is '\r' or '\n')
                {
                    if (line.Length > 0)
                    {
                        var s = line.ToString();
                        line.Clear();
                        if (onPercent is not null && TryPercent(s, out var pct)) onPercent(pct);
                        else
                        {
                            lock (gate) { if (sb.Length < 8000) sb.AppendLine(s); }
                        }
                    }
                    continue;
                }
                if (line.Length < 2048) line.Append(c);
            }
        }
        if (line.Length > 0) { lock (gate) { if (sb.Length < 8000) sb.AppendLine(line.ToString()); } }
    }

    private static bool TryPercent(string line, out int pct)
    {
        pct = 0;
        var idx = line.IndexOf('%');
        if (idx <= 0) return false;
        var start = idx - 1;
        while (start >= 0 && line[start] == ' ') start--;
        var end = start;
        while (start >= 0 && char.IsDigit(line[start])) start--;
        if (end <= start) return false;
        return int.TryParse(line.AsSpan(start + 1, end - start), out pct) && pct is >= 0 and <= 100;
    }

    /// <summary>
    /// 新建一个桶。**注意 <c>-p</c> 与密码必须在同一个参数里**，且绝不能进日志。
    /// <list type="bullet">
    /// <item><c>-mx0</c> 不压缩：视频压不动，省一半时间</item>
    /// <item><c>-ms=off</c> 非固实：一个条目损坏不牵连整桶，而且能单条取出</item>
    /// <item><c>-mhe=on</c> 加密文件名：否则包内标题是明文，比内容泄露更容易被读懂</item>
    /// </list>
    /// </summary>
    public static Task<Result> CreateAsync(string exe, string archivePath, string listFile,
        string password, string workingDir, Action<int>? onPercent, CancellationToken ct) =>
        RunAsync(exe, new[]
        {
            "a", "-t7z", "-mx0", "-ms=off", "-mhe=on", "-y", "-bsp1", "-bse1",
            "-p" + password, archivePath, "@" + listFile,
        }, workingDir, onPercent, ct);

    /// <summary>
    /// 解包。<paramref name="include"/> 非空时只取这些前缀（<c>-i!</c>）——
    /// 恢复一个 2 GiB 的桶只为了拿回其中 300 MB 时，这一条能把临时目录峰值和 IO 都压下来。
    /// <c>-ms=off</c>（非固实）正是为了让这一步可行。
    /// </summary>
    public static Task<Result> ExtractAsync(string exe, string archivePath, string outDir,
        string password, Action<int>? onPercent, CancellationToken ct,
        IEnumerable<string>? include = null)
    {
        var args = new List<string> { "x", "-y", "-bsp1", "-bse1", "-p" + password, "-o" + outDir };
        if (include is not null)
            foreach (var p in include)
                if (!string.IsNullOrWhiteSpace(p)) args.Add("-i!" + p);
        args.Add(archivePath);
        return RunAsync(exe, args, null, onPercent, ct);
    }

    /// <summary>完整性测试（逐文件 CRC 校验），比"能解开第一个文件"强得多。</summary>
    public static Task<Result> TestAsync(string exe, string archivePath, string password,
        Action<int>? onPercent, CancellationToken ct) =>
        RunAsync(exe, new[] { "t", "-y", "-bsp1", "-bse1", "-p" + password, archivePath },
            null, onPercent, ct);

    /// <summary>
    /// 错密码 vs 别的错误要分开：前者让用户重输，后者要报具体原因。
    /// 7z 在错密码时的固定文案是 "Wrong password" / "Can not open encrypted archive"。
    /// </summary>
    public static bool LooksLikeWrongPassword(Result r)
    {
        var s = r.Output ?? "";
        return s.Contains("Wrong password", StringComparison.OrdinalIgnoreCase)
            || s.Contains("encrypted archive", StringComparison.OrdinalIgnoreCase)
            || s.Contains("Cannot open encrypted", StringComparison.OrdinalIgnoreCase)
            || s.Contains("Data Error in encrypted file", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>流式算单个文件的 sha256（恢复/校验逐文件比对用，不整读进内存）。</summary>
    public static string Sha256File(string path, CancellationToken ct)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1 << 20, FileOptions.SequentialScan);
        using var sha = System.Security.Cryptography.SHA256.Create();
        var buf = new byte[1 << 20];
        int n;
        while ((n = fs.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            sha.TransformBlock(buf, 0, n, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }
}
