using System.Runtime.InteropServices;

namespace HandPilot;

public static class DesktopGeometry
{
    public static int Left => GetSystemMetrics(76);
    public static int Top => GetSystemMetrics(77);
    public static int Width => Math.Max(1, GetSystemMetrics(78));
    public static int Height => Math.Max(1, GetSystemMetrics(79));

    public static HandPoint Pointer()
    {
        if (!GetCursorPos(out var point)) throw new InvalidOperationException("Could not read the desktop cursor.");
        return new(Math.Clamp((point.X - Left) / (double)Math.Max(1, Width - 1), 0, 1),
            Math.Clamp((point.Y - Top) / (double)Math.Max(1, Height - 1), 0, 1));
    }

    public static string Key()
    {
        var displays = new List<string>();
        MonitorCallback callback = (IntPtr monitor, IntPtr dc, ref Rect rectangle, IntPtr data) =>
        {
            displays.Add($"{rectangle.Left},{rectangle.Top},{rectangle.Right},{rectangle.Bottom}");
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        displays.Sort(StringComparer.Ordinal);
        return string.Join(";", displays);
    }

    public static void CoverDesktop(IntPtr handle) =>
        SetWindowPos(handle, new IntPtr(-1), Left, Top, Width, Height, 0x0040);

    private delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, ref Rect rectangle, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
}
