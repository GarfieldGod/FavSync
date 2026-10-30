using System.Diagnostics;
using FavSync.Core.Config;
using FavSync.Server;

namespace FavSync.Desktop;

/// <summary>
/// 托盘本身。没有主窗口——这是刻意的：服务在跑的时候占一块屏幕没有意义，
/// 需要管理时点一下就开浏览器。
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly EventWaitHandle _showEvent;
    private readonly ServerRunner _server = new();
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>Dispose 只做一次（见 Dispose(bool) 的注释）。</summary>
    private bool _disposed;

    /// <summary>
    /// 把线程池上的调用搬回 UI 线程用的载体。ApplicationContext 不是 Control，没有 Invoke/BeginInvoke；
    /// 而 SynchronizationContext.Current 在构造函数里是 null（Application.Run 之前还没装 WinForms 上下文），
    /// 抓它只会抓到默认上下文 —— 那样 ReferenceEquals 永远不成立，重启请求会在 Post 里打转。
    /// 所以用一个显式 Control：先创建句柄，之后 InvokeRequired/BeginInvoke 才有意义。
    /// </summary>
    private readonly Control _marshaller = new();

    private bool _initialized;

    public TrayApp(EventWaitHandle showEvent)
    {
        _showEvent = showEvent;
        _icon = new NotifyIcon
        {
            Icon = AppIcon.Create(),
            Visible = true,
            Text = "FavSync",
            ContextMenuStrip = BuildMenu(),
        };

        // 双击图标 = 打开管理页，与 Steam 那种"点一下回到界面"的习惯一致。
        _icon.DoubleClick += (_, _) => OpenBrowser();

        // 设置页点「重启服务」时，由服务内部的 IServerControl 转到这里真正干活
        _server.RestartRequested += RestartAsync;

        // 强制建句柄：没有句柄时 Control.InvokeRequired 恒为 false，跨线程就直接跑错线程了
        _ = _marshaller.Handle;

        Application.Idle += OnFirstIdle;
        ListenForSecondInstance();
    }

    private void OnFirstIdle(object? sender, EventArgs e)
    {
        if (_initialized) return;

        _initialized = true;
        Application.Idle -= OnFirstIdle;

        // 放到 Idle 里做：构造函数里同步等 HTTP 管线启动会把消息循环卡住，
        // 托盘图标要等半天才出现，看起来像程序没启动。
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        // 首次运行（还没有配置文件）= 用户刚把程序解压出来、双击了 exe。
        // 这时候把管理页直接推到他面前，而不是让他去托盘里找图标。
        // 开机自启那类"第 N 次启动"不弹浏览器，免得每次登录都多一个标签页。
        var firstRun = !File.Exists(FavSyncConfig.ConfigPath);

        var ok = await _server.StartAsync();

        if (ok)
        {
            if (firstRun) OpenBrowser();
        }
        else
        {
            // **服务起不来必须让用户看见**。以前只有 3 秒气泡，用户看到的只是
            // "页面打不开"，根本不知道是端口被占、还是库目录有问题。
            ShowStartupFailure();
        }

        UpdateTray();
    }

    private void ShowStartupFailure()
    {
        var reason = _server.LastError ?? "未知原因";
        var text = $"""
                    管理页没能启动，所以浏览器打开是"无法访问"。

                    原因：{reason}

                    常见情况：
                    · 端口被占用 —— 打开配置文件把 Port 改成别的（如 8730）后重启
                    · 库目录不可用 —— 打开配置文件改 ArchiveRoot，或在能打开的管理页「设置」里改

                    配置：{FavSyncConfig.ConfigPath}
                    日志：{ServerRunner.LogPath}
                    """;

        MessageBox.Show(text, "FavSync 启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        _icon.ShowBalloonTip(8000, "FavSync 启动失败", reason, ToolTipIcon.Error);
    }

    /// <summary>设置页改完库地址/端口后重启用。可能从线程池进来，统一回 UI 线程再动控件。</summary>
    private async Task RestartAsync()
    {
        if (_disposed) return;

        if (_marshaller.InvokeRequired)
        {
            try { _marshaller.BeginInvoke(new Action(() => _ = RestartAsync())); } catch (ObjectDisposedException) { }
            return;
        }

        var ok = await _server.StartAsync();
        UpdateTray();

        if (ok)
            _icon.ShowBalloonTip(3000, "FavSync 已重启", $"管理页：{_server.PrimaryUrl}", ToolTipIcon.Info);
        else
            ShowStartupFailure();
    }

    // ── 菜单 ────────────────────────────────────────────────

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        var open = new ToolStripMenuItem("打开管理页", null, (_, _) => OpenBrowser());
        open.Font = new Font(open.Font, FontStyle.Bold);

        var copy = new ToolStripMenuItem("复制地址", null, (_, _) => CopyUrl());
        // 手工改过配置文件（端口/库目录）后不用退出程序，从这儿重启一次即可
        var restart = new ToolStripMenuItem("重新启动服务", null, (_, _) => _ = RestartAsync());

        var openArchive = new ToolStripMenuItem("打开总库目录", null, (_, _) => OpenFolder(ArchiveRoot));
        var openConfig = new ToolStripMenuItem("打开配置文件", null, (_, _) => OpenFile(FavSyncConfig.ConfigPath));
        var openLog = new ToolStripMenuItem("打开日志", null, (_, _) => OpenFile(ServerRunner.LogPath));

        // CheckOnClick + 注册表同步状态：菜单弹出时刷新，防止用户手动改过注册表后菜单撒谎。
        var autoStart = new ToolStripMenuItem("开机自启", null, (_, _) => ToggleAutoStart())
        {
            CheckOnClick = true,
            Checked = SafeIsAutoStart(),
        };

        var exit = new ToolStripMenuItem("退出", null, (_, _) => ExitApplication());

        menu.Items.Add(open);
        menu.Items.Add(copy);
        menu.Items.Add(restart);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(openArchive);
        menu.Items.Add(openConfig);
        menu.Items.Add(openLog);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(autoStart);
        menu.Items.Add(exit);

        // 打开菜单时刷新一次，避免显示上一分钟的状态。
        menu.Opening += (_, _) =>
        {
            UpdateTray();
            autoStart.Checked = SafeIsAutoStart();
        };
        return menu;
    }

    private static string ArchiveRoot
    {
        get
        {
            try { return FavSyncConfig.Load().EffectiveArchiveRoot; }
            catch { return FavSyncConfig.DefaultArchiveRoot; }
        }
    }

    /// <summary>注册表读取理论上不抛，但 Explorer 重启等边缘态不值得让它炸托盘。</summary>
    private static bool SafeIsAutoStart()
    {
        try { return AutoStart.IsEnabled(); }
        catch { return false; }
    }

    private void ToggleAutoStart()
    {
        try
        {
            AutoStart.SetEnabled(!AutoStart.IsEnabled());
        }
        catch (Exception ex)
        {
            MessageBox.Show($"修改开机自启失败：{ex.Message}", "FavSync",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UpdateTray()
    {
        _icon.Text = Truncate(_server.Running
            ? $"FavSync · 运行中 · {new Uri(_server.PrimaryUrl!).Port}"
            : "FavSync · 已停止");
    }

    /// <summary>NotifyIcon.Text 上限 63 字符，超了会抛异常。</summary>
    private static string Truncate(string text) => text.Length <= 62 ? text : text[..62];

    // ── 外部动作 ─────────────────────────────────────────────

    private void OpenBrowser()
    {
        var url = _server.PrimaryUrl;
        if (url is null)
        {
            _icon.ShowBalloonTip(3000, "FavSync", "服务没在运行。", ToolTipIcon.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打不开浏览器：{ex.Message}\n\n请手动访问 {url}", "FavSync",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void CopyUrl()
    {
        var url = _server.PrimaryUrl;
        if (url is null) return;

        try
        {
            Clipboard.SetText(url);
            _icon.ShowBalloonTip(2000, "FavSync", $"已复制：{url}", ToolTipIcon.Info);
        }
        catch (Exception)
        {
            // 剪贴板被别的进程占用时会抛，重试没有意义。
        }
    }

    private static void OpenFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            MessageBox.Show($"目录不存在：{path}", "FavSync", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private static void OpenFile(string path)
    {
        if (!File.Exists(path))
        {
            MessageBox.Show($"文件不存在：{path}", "FavSync", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    // ── 生命周期 ─────────────────────────────────────────────

    /// <summary>第二个实例启动时置位这个事件，我们来开浏览器——用户看到的就像"点一下就打开了"。</summary>
    private void ListenForSecondInstance()
    {
        _ = Task.Run(() =>
        {
            while (!_shutdown.IsCancellationRequested)
            {
                try
                {
                    if (!_showEvent.WaitOne(500)) continue;
                    OpenBrowser();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        });
    }

    private void ExitApplication()
    {
        _shutdown.Cancel();
        _icon.Visible = false;

        // 用 Task.Run 把 StopAsync 从 UI 线程挪开：Application.Exit 会等 UI 线程，
        // 同步等 Kestrel 停止可能超时（5s）。
        Task.Run(async () =>
        {
            await _server.StopAsync();
        }).Wait(TimeSpan.FromSeconds(6));

        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        // Dispose 会被调两次（Application.Run 退出时一次、Main 里 using 再一次）。
        // 第二次时 _shutdown 已经 Dispose 过，Cancel() 会抛 ObjectDisposedException 冒到 Main 外，
        // 用户看到的就是"退出托盘 → 一屏红色 Unhandled exception"。
        if (disposing && !_disposed)
        {
            _disposed = true;
            try { _shutdown.Cancel(); } catch (ObjectDisposedException) { }
            _icon.Visible = false;
            _icon.Dispose();
            _shutdown.Dispose();
            _marshaller.Dispose();
        }

        base.Dispose(disposing);
    }
}
