using System.Diagnostics;
using System.Text;

namespace FavSync.Core.Adb;

public sealed record AdbDevice(string Serial, string State, string? Model)
{
    public bool IsOnline => State.Equals("device", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// adb 的唯一入口。所有设备访问都必须经过这里，禁止别处再拼 Process。
/// 关键约定：脚本一律以 <c>sh -c '...'</c> 形式作为**单个参数**传给 adb，
/// 因为 adb 会把 argv 用空格拼成一条命令串再交给设备 shell，参数边界会丢。
/// 所以脚本内部只能用双引号，不能出现单引号。
/// </summary>
public sealed class AdbClient
{
    public string AdbPath { get; }

    public AdbClient(string adbPath) => AdbPath = adbPath;

    public static string? Resolve(string? configured)
    {
        var candidates = new List<string?>();

        if (!string.IsNullOrWhiteSpace(configured)) candidates.Add(configured);
        candidates.Add(Environment.GetEnvironmentVariable("FAVSYNC_ADB"));

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var exe = Path.Combine(dir.Trim(), OperatingSystem.IsWindows() ? "adb.exe" : "adb");
            if (File.Exists(exe)) { candidates.Add(exe); break; }
        }

        // 随程序自带的那份 adb（发布包里 tools\adb\adb.exe）。排在 PATH 之后、其它约定路径之前：
        // 用户自己配的环境优先；没配时也能直接用自带的，不必为了跑起来去装 platform-tools。
        // 旧布局（产物根下的 adb\）留着兜底：早期解压包就地换 exe 时不会突然找不到 adb。
        if (OperatingSystem.IsWindows())
        {
            candidates.Add(Path.Combine(AppContext.BaseDirectory, "tools", "adb", "adb.exe"));
            candidates.Add(Path.Combine(AppContext.BaseDirectory, "adb", "adb.exe"));
            candidates.Add(Path.Combine(AppContext.BaseDirectory, "platform-tools", "adb.exe"));
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        candidates.Add(Path.Combine(home, "tools", "platform-tools", "adb.exe"));
        candidates.Add(Path.Combine(home, "AppData", "Local", "Android", "Sdk", "platform-tools", "adb.exe"));
        candidates.Add(@"C:\Android\platform-tools\adb.exe");

        return candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && File.Exists(c));
    }

    private ProcessStartInfo NewPsi()
    {
        var psi = new ProcessStartInfo(AdbPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        return psi;
    }

    public IReadOnlyList<AdbDevice> ListDevices()
    {
        var psi = NewPsi();
        psi.ArgumentList.Add("devices");
        psi.ArgumentList.Add("-l");

        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(15_000);

        var list = new List<AdbDevice>();
        foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("List of devices", StringComparison.Ordinal)) continue;
            if (line.StartsWith("* ", StringComparison.Ordinal)) continue;

            // `adb devices -l` 用空格分隔，`adb devices` 用 \t —— 统一按任意空白切
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            var serial = parts[0].Trim();
            var state = parts[1].Trim();
            string? model = null;
            foreach (var seg in parts.Skip(2))
            {
                var kv = seg.Split(':');
                if (kv.Length == 2 && kv[0] == "model") model = kv[1];
            }
            list.Add(new AdbDevice(serial, state, model));
        }
        return list;
    }

    /// <summary>无线连接手机：adb connect &lt;IP&gt;[:端口]（省略端口默认 5555）。返回 adb 原始输出。</summary>
    public string Connect(string address)
    {
        address = (address ?? string.Empty).Trim();
        if (address.Length == 0) throw new ArgumentException("地址不能为空", nameof(address));
        var psi = NewPsi();
        psi.ArgumentList.Add("connect");
        psi.ArgumentList.Add(address);
        using var p = Process.Start(psi)!;
        return DecodeSmart(RunToEnd(p));
    }

    /// <summary>断开：address 为空断全部，否则只断指定设备。</summary>
    public string Disconnect(string? address)
    {
        var psi = NewPsi();
        psi.ArgumentList.Add("disconnect");
        if (!string.IsNullOrWhiteSpace(address)) psi.ArgumentList.Add(address.Trim());
        using var p = Process.Start(psi)!;
        return DecodeSmart(RunToEnd(p));
    }

    private static byte[] RunToEnd(Process p)
    {
        using var ms = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(ms);   // 先读流再 WaitForExit，避免管道死锁
        p.WaitForExit(30_000);
        return ms.ToArray();
    }

    /// <summary>
    /// adb 是 Windows 控制台程序，输出走本机代码页（简体中文 = GBK/936）。
    /// 直接按 UTF-8 解会把中文变"鐢变簬"这类乱码 —— 先严格 UTF-8，失败回退 GBK，再不行用系统默认。
    /// </summary>
    private static string DecodeSmart(byte[] bytes)
    {
        if (bytes.Length == 0) return string.Empty;
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch { }
        try { return Encoding.GetEncoding(936).GetString(bytes); }
        catch { }
        return Encoding.Default.GetString(bytes);
    }

    /// <summary>
    /// 流式读取脚本输出，逐行回调。
    /// 必须用它而不是"跑完再 ReadToEnd"：全量元数据要跑好几分钟，
    /// 等 ReadToEnd 时界面看起来就是卡死，而且拿不到真实进度。
    /// </summary>
    public async Task ShellLinesAsync(
        string serial,
        string script,
        Action<string> onLine,
        CancellationToken ct = default,
        int timeoutMs = 40 * 60 * 1000)
    {
        var psi = NewPsi();
        psi.ArgumentList.Add("-s");
        psi.ArgumentList.Add(serial);
        psi.ArgumentList.Add("exec-out");
        psi.ArgumentList.Add(ShellQuote.Wrap(script));

        using var p = new Process { StartInfo = psi };
        p.Start();

        var errTask = p.StandardError.ReadToEndAsync(ct);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);

        try
        {
            while (true)
            {
                var line = await p.StandardOutput.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false);
                if (line is null) break;
                onLine(line);
            }
            await p.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        var err = await errTask.ConfigureAwait(false);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"adb 扫描失败（exit {p.ExitCode}）：{err}");
    }

    /// <summary>
    /// 打开 exec-out 的原始输出流（二进制安全，tar 流走这里）。
    /// 为什么不用逐行 ShellLinesAsync 读大流量：逐行 printf 产生海量小写入，
    /// 经 adbd/TLS 逐条封装后在 TCP 上触发 Nagle/延迟 ACK 乒乓
    /// （实测：Tailscale 上逐行 51 ms/文件，tar 单流 7 ms/文件）。
    /// 用法：读完 EOF 后调用 <see cref="AdbOutputStream.FinishAsync"/> 校验退出码。
    /// </summary>
    public AdbOutputStream OpenOutput(string serial, string script)
    {
        var psi = NewPsi();
        psi.ArgumentList.Add("-s");
        psi.ArgumentList.Add(serial);
        psi.ArgumentList.Add("exec-out");
        psi.ArgumentList.Add(ShellQuote.Wrap(script));

        var p = new Process { StartInfo = psi };
        p.Start();
        return new AdbOutputStream(p);
    }

    /// <summary>包装 adb exec-out 的标准输出。读完 EOF 后 Finish 校验退出码；Dispose 回收进程。</summary>
    public sealed class AdbOutputStream : Stream
    {
        private readonly Process _p;
        private readonly Task<string> _err;
        private readonly Stream _inner;

        internal AdbOutputStream(Process p)
        {
            _p = p;
            _inner = p.StandardOutput.BaseStream;
            _err = p.StandardError.ReadToEndAsync();
        }

        /// <summary>等待进程退出并校验退出码。失败（含脚本 exit 3）时抛出，携带 stderr。</summary>
        public async Task FinishAsync(CancellationToken ct = default)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(30_000);
            try
            {
                await _p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { _p.Kill(entireProcessTree: true); } catch { }
                throw;
            }

            var err = await _err.ConfigureAwait(false);
            if (_p.ExitCode != 0)
                throw new InvalidOperationException($"adb exec-out 失败（exit {_p.ExitCode}）：{err}");
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            _inner.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { if (!_p.HasExited) _p.Kill(entireProcessTree: true); } catch { }
                _p.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>把命令的原始字节流输出到目标流。用于 tar 打包整目录。</summary>
    public async Task PipeToAsync(string serial, string script, Stream destination, CancellationToken ct = default)
    {
        var psi = NewPsi();
        psi.ArgumentList.Add("-s");
        psi.ArgumentList.Add(serial);
        psi.ArgumentList.Add("exec-out");
        psi.ArgumentList.Add(ShellQuote.Wrap(script));

        using var p = new Process();
        p.StartInfo = psi;
        p.Start();

        var errTask = p.StandardError.ReadToEndAsync(ct);
        try
        {
            await p.StandardOutput.BaseStream.CopyToAsync(destination, 1 << 16, ct).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (!p.HasExited) p.WaitForExit(5_000);
            }
            catch { }
        }

        var err = await errTask.ConfigureAwait(false);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"adb exec-out 失败（exit {p.ExitCode}）：{err}");
    }
}

public static class ShellQuote
{
    /// <summary>
    /// 生成传给 adb 的单个参数：sh -c '...'
    /// 脚本内不得含单引号 —— 违反时在这里抛，而不是让设备 shell 悄悄截断。
    /// </summary>
    public static string Wrap(string script)
    {
        if (script.Contains('\''))
            throw new ArgumentException("脚本内不允许出现单引号（adb 参数边界会丢）", nameof(script));
        return $"sh -c '{script}'";
    }
}
