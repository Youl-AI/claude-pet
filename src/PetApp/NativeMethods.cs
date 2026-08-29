using System;
using System.Runtime.InteropServices;
using System.Text;
using PetCore;

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

    // 바탕화면을 식별하는 공식 방법. "Progman"이라는 클래스 이름으로
    // 문자열 매칭하는 대신 이 핸들과 비교한다 — 문서화된 API고,
    // WorkerW처럼 바탕화면을 호스팅할 수 있는 다른 창까지 함께
    // 잡아내지도 않는다(그건 클래스 이름 목록에서 별도로 걸러낸다).
    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

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

    /// <summary>
    /// 전체화면 앱(발표·영상·게임)이 앞에 있으면 펫은 숨어야 한다 — 단,
    /// 그 앱이 펫과 같은 모니터에 있을 때만이다. 다중 모니터 환경에서
    /// 다른 모니터를 뒤덮는 창은 펫이 있는 화면을 전혀 가리지 않는다.
    ///
    /// <paramref name="petHwnd"/>로 펫 자신의 모니터를 <c>MonitorFromWindow</c>
    /// 로 구해 전경 창의 모니터와 비교한다. 판정 로직 자체(사각형이
    /// 모니터를 뒤덮는지, 데스크톱/셸 창은 아닌지, 같은 모니터인지)는
    /// FullscreenWindowClassifier로 뽑아 두었다 — 여기서는 P/Invoke로
    /// 원재료(전경 창 핸들, 셸 창 여부, 클래스 이름, 창/모니터 사각형,
    /// 모니터 핸들 비교)만 모아 넘긴다. 판정 로직을 Windows API와
    /// 분리해야 유닛 테스트가 가능하고, 그 테스트가 실제로
    /// 바탕화면(Progman)이 항상 "전체화면"으로 오판되던 버그와, 다른
    /// 모니터를 덮는 창 때문에 펫이 숨어버리던 버그를 잡아낸다.
    /// </summary>
    public static bool IsFullscreenAppForeground(IntPtr petHwnd)
    {
        try
        {
            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero) return false;
            if (!GetWindowRect(foreground, out var windowRect)) return false;

            var monitor = MonitorFromWindow(foreground, 2 /* MONITOR_DEFAULTTONEAREST */);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref info)) return false;

            var petMonitor = MonitorFromWindow(petHwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
            var isSameMonitorAsPet = monitor == petMonitor;

            var isShellWindow = foreground == GetShellWindow();

            var classNameBuffer = new StringBuilder(256);
            GetClassName(foreground, classNameBuffer, classNameBuffer.Capacity);

            return FullscreenWindowClassifier.IsFullscreenApp(
                ToPixelRect(windowRect),
                ToPixelRect(info.rcMonitor),
                classNameBuffer.ToString(),
                isShellWindow,
                isSameMonitorAsPet);
        }
        catch (Exception)
        {
            // 이 메서드의 계약은 "절대 던지지 않는다"이다 — 매초 폴링되는
            // 전체화면 검사가 예외를 던지면 펫 전체가 죽는다. 방해 0
            // 제약상 예외 종류를 하나씩 열거하기보다(그 방식은 이
            // 프로젝트에서 이미 실패한 적 있다 — WindowsProcessProbe 참고)
            // 이 메서드 경계 전체를 catch-all로 감싸 구조적으로 보장한다.
            // 판정 실패 시엔 숨기지 않는 쪽(false)이 안전하다 — 최악의
            // 대가는 펫이 전체화면 앱 위에 계속 보이는 것뿐이고, 그 반대
            // (오탐으로 계속 숨음)가 사용자 경험상 더 나쁘다.
            return false;
        }
    }

    /// <summary>
    /// 워킹셋을 OS 에 반환한다. 콜드 스캔이 끝난 직후 한 번만 부른다 — 스캔이
    /// 일시적으로 끌어올린 페이지를 돌려줘서 작업 관리자 수치가 실사용을
    /// 반영하게 한다. 자주 부르면 페이지 폴트만 늘어나므로 반복 호출하지 않는다.
    /// 실패해도 아무 일도 하지 않는다 — 장식일 뿐 기능이 아니다.
    /// </summary>
    public static void TrimWorkingSet()
    {
        try
        {
            EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle);
        }
        catch (Exception)
        {
        }
    }

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr process);

    private static PixelRect ToPixelRect(RECT rect) => new(rect.Left, rect.Top, rect.Right, rect.Bottom);
}
