using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace FavSync.Desktop;

/// <summary>
/// 程序内画一个托盘图标，不额外带 .ico 文件。
/// 这样发布时不用操心资源路径，也不会出现"图标丢了变成白方块"。
/// </summary>
internal static class AppIcon
{
    public static Icon Create()
    {
        using var bitmap = new Bitmap(32, 32);

        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var brush = new SolidBrush(Color.FromArgb(0x2E, 0x8B, 0x57));
            g.FillEllipse(brush, 1, 1, 29, 29);

            // 一支向下的箭头（落进托盘的横线），点题"把手机上的缓存收进总库"。
            using var white = new SolidBrush(Color.White);
            // 箭杆
            g.FillRectangle(white, 14, 7, 4, 11);
            // 箭头（三角）
            g.FillPolygon(white, [new PointF(9, 17), new PointF(23, 17), new PointF(16, 24)]);
            // 底部托线
            g.FillRectangle(white, 9, 25, 14, 3);
        }

        var handle = bitmap.GetHicon();

        try
        {
            // Clone 之后 Icon 不再依赖这个 HICON，可以安全销毁原始句柄。
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
