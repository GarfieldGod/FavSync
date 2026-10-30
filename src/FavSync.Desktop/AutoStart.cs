using Microsoft.Win32;

namespace FavSync.Desktop;

/// <summary>
/// 开机自启 = HKCU 的 Run 键，不需要管理员权限。
/// 路径存当前 exe 的绝对路径——发布版和 Debug 版各自记录自己的位置，互不干扰。
/// </summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FavSync";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string path &&
               string.Equals(path.Trim('"'), EntryPath(), StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);

        if (enabled)
        {
            key.SetValue(ValueName, $"\"{EntryPath()}\"");
        }
        else if ((key.GetValue(ValueName) is not null))
        {
            key.DeleteValue(ValueName);
        }
    }

    private static string EntryPath() => Environment.ProcessPath
        ?? throw new InvalidOperationException("拿不到当前 exe 路径");
}
