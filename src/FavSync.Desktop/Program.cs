namespace FavSync.Desktop;

internal static class Program
{
    /// <summary>
    /// 单实例锁。命名要带 <c>Local\</c>：不带前缀的全局对象在终端服务多会话下会互相干扰，
    /// 而"这台机器上的这个人"恰好就是我们要互斥的范围。
    /// </summary>
    private const string MutexName = @"Local\FavSync.Desktop.Instance";

    /// <summary>第二个实例用它叫醒第一个实例去开浏览器。</summary>
    private const string ShowEventName = @"Local\FavSync.Desktop.Show";

    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);

        if (!isFirstInstance)
        {
            // 已经有一个在托盘里了。用户双击 exe 的意图是"把界面弄出来"，不是"再开一个服务"。
            SignalExistingInstance();
            return;
        }

        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        using var app = new TrayApp(showEvent);

        Application.Run(app);
    }

    private static void SignalExistingInstance()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(ShowEventName, out var handle)) return;

            using (handle) handle.Set();
        }
        catch (Exception)
        {
            // 叫不醒就静默退出：最坏情况是用户再点一次托盘图标，不值得弹框打扰。
        }
    }
}
