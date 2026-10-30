using FavSync.Core.Config;
using FavSync.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FavSync.Desktop;

/// <summary>
/// 托盘壳对服务生命周期的全部了解。界面只跟它打交道，不直接碰 Kestrel。
/// <para>
/// 单进程形态：WebApplication 就跑在这个 WinForms 进程里。
/// 所以"退出托盘"= 服务真的停了，不存在"关了窗口服务还在后台跑"的那种分裂状态。
/// </para>
/// </summary>
internal sealed class ServerRunner
{
    private WebApplication? _app;

    public bool Running => _app is not null;

    /// <summary>启动失败的原因。<b>必须让用户看见</b>（弹窗 + 日志），否则表现就是"页面打不开"。</summary>
    public string? LastError { get; private set; }

    /// <summary>点托盘"打开管理页"时去哪个地址。</summary>
    public string? PrimaryUrl { get; private set; }

    /// <summary>桌面壳的日志位置。没有控制台，出问题只能看这个文件。</summary>
    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FavSync", "logs", "desktop.log");

    /// <summary>
    /// 设置页改完库地址/端口后，服务需要"停→起"。真正执行重启动的是托盘壳（它管着 UI 刷新），
    /// 所以这里只发个请求出去。
    /// </summary>
    public event Func<Task>? RestartRequested;

    /// <summary>
    /// 按配置启动（或重启）。返回 false 时 <see cref="LastError"/> 有原因，
    /// <b>不要吞掉它</b>——最常见的是端口被占用，用户需要知道是哪个端口。
    /// </summary>
    public async Task<bool> StartAsync()
    {
        await StopAsync();
        LastError = null;

        try
        {
            var cfg = FavSyncConfig.Load();
            PrimaryUrl = $"http://127.0.0.1:{cfg.Port}";

            var app = FavSync.Server.Program.CreateApp(["--urls", PrimaryUrl], builder =>
            {
                // WinExe 没有控制台：默认 Console provider 写出去的中文是乱码，且用户看不到。
                builder.Logging.ClearProviders();
                builder.Logging.AddProvider(new FileLoggerProvider(LogPath));
                builder.Logging.SetMinimumLevel(LogLevel.Information);

                // 让 /api/v1/config/restart 有能力真的重启这个进程里的服务
                builder.Services.AddSingleton<IServerControl>(new DesktopServerControl(this));
            });

            await app.StartAsync();
            _app = app;
            WriteLog($"服务已启动：{PrimaryUrl}（总库 {cfg.EffectiveArchiveRoot}）");
            if (cfg.BootNotice is not null) WriteLog("⚠ " + cfg.BootNotice);
            return true;
        }
        catch (Exception ex)
        {
            var root = ex.GetBaseException();
            LastError = root.Message;

            // 光有 LastError 没用：托盘气泡 3 秒就没了，用户看到的只有"页面打不开"。
            // 这里必须留一条能查的痕迹，写上端口和配置位置，照着就能自己定位。
            WriteLog($"服务启动失败（{PrimaryUrl ?? "未定地址"}）：{root}\n配置：{FavSyncConfig.ConfigPath}");

            await StopAsync();
            PrimaryUrl = null;
            return false;
        }
    }

    public async Task StopAsync()
    {
        if (_app is null) return;

        var app = _app;
        _app = null;

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await app.StopAsync(timeout.Token);
        }
        catch (Exception)
        {
            // 停止阶段的异常不该阻止进程退出：Kestrel 在端口已被释放时可能抛。
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    /// <summary>追加一行到 desktop.log（用 UTF-8 追加，失败也不抛 —— 日志烂掉不能拖垮托盘）。</summary>
    public static void WriteLog(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath,
                $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====\n{message}\n\n",
                new System.Text.UTF8Encoding(false));
        }
        catch { }
    }

    /// <summary>
    /// 重启要等当前这次 HTTP 响应先写回去，否则浏览器拿到的是被掐断的连接、
    /// 前端只会显示"网络错误"，看不到"正在重启"的提示。
    /// </summary>
    public void BeginRestart()
    {
        var handler = RestartRequested;
        _ = Task.Run(async () =>
        {
            await Task.Delay(600);
            if (handler is null) return;
            try { await handler(); }
            catch (Exception ex) { WriteLog("重启失败：" + ex); }
        });
    }

    /// <summary>给 <see cref="IServerControl"/> 用的门面：只暴露"能不能重启 / 请求重启"。</summary>
    private sealed class DesktopServerControl : IServerControl
    {
        private readonly ServerRunner _owner;

        public DesktopServerControl(ServerRunner owner) => _owner = owner;

        public bool CanRestart => true;

        public bool RequestRestart()
        {
            _owner.BeginRestart();
            return true;
        }
    }
}
