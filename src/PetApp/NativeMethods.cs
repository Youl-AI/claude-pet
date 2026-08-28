using System;
using System.Runtime.InteropServices;

namespace PetApp;

internal static class NativeMethods
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;   // 마우스 클릭 통과
    private const int WS_EX_TOOLWINDOW  = 0x00000080;   // Alt+Tab 목록에서 제외
    private const int WS_EX_NOACTIVATE  = 0x08000000;   // 절대 포커스를 받지 않음

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    /// <summary>
    /// 창이 포커스를 훔치지도, 클릭을 가로막지도, Alt+Tab에 나타나지도 않게 만든다.
    /// 방해 0 제약의 직접 구현이다.
    /// </summary>
    public static void MakeNonInteractive(IntPtr hwnd)
    {
        var style = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE,
            style | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    /// <summary>전체화면 앱(발표·영상·게임)이 앞에 있으면 펫은 숨어야 한다.</summary>
    public static bool IsFullscreenAppForeground()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        if (!GetWindowRect(foreground, out var windowRect)) return false;

        var monitor = MonitorFromWindow(foreground, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;

        var m = info.rcMonitor;
        return windowRect.Left <= m.Left && windowRect.Top <= m.Top
            && windowRect.Right >= m.Right && windowRect.Bottom >= m.Bottom;
    }
}
