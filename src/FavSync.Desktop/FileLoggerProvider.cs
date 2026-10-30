using System.Text;
using Microsoft.Extensions.Logging;

namespace FavSync.Desktop;

/// <summary>
/// 极简文件日志。
/// <para>
/// 存在的理由很具体：桌面壳是 WinExe，<b>没有控制台</b>，默认的 Console provider 会把中文按
/// 系统代码页写出去（重定向到文件时直接是乱码），用户又看不到窗口。
/// 一旦服务起不来，没有任何线索可查。
/// </para>
/// <para>
/// 不做轮转、不做异步队列：一天几百行，涨不到需要管的地步。真涨起来了再加。
/// </para>
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _path;
    private readonly object _gate = new();

    public FileLoggerProvider(string path)
    {
        _path = path;

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose() { }

    private void Write(string line)
    {
        lock (_gate)
        {
            try
            {
                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException)
            {
                // 日志写不进去不该拖垮服务。
            }
        }
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var shortCategory = category.Split('.').LastOrDefault() ?? category;
            var text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{logLevel,-11}] {shortCategory}: {formatter(state, exception)}";

            if (exception is not null) text += Environment.NewLine + exception;

            owner.Write(text);
        }
    }
}
