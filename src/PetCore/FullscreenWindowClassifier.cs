namespace PetCore;

/// <summary>
/// 전경 창이 "진짜" 전체화면 앱인지 판정하는 순수 함수. 사각형 두 개, 클래스
/// 이름 문자열 하나, 셸 창 여부 불리언 하나만 받는 순수 데이터 계산이라
/// Windows API를 전혀 참조하지 않는다 — PetCore의 플랫폼 중립성을 해치지
/// 않는다. 실제 P/Invoke(전경 창 핸들 조회, GetShellWindow, GetClassName,
/// 사각형 조회)는 PetApp.NativeMethods가 담당하고, 그 결과값만 이 함수에
/// 넘긴다. 그래서 이 판정 로직만 따로 유닛 테스트할 수 있다.
/// </summary>
public static class FullscreenWindowClassifier
{
    // 바탕화면·작업 표시줄을 호스팅하는 셸 창 클래스들. Progman은 항상
    // 가상 데스크톱 전체 크기이고, WorkerW는 벽지 설정에 따라 바탕화면을
    // 호스팅하며 모니터 전체 크기가 될 수 있다 — 그래서 크기가 아니라
    // 클래스 이름으로 걸러낸다.
    private static readonly string[] ShellWindowClassNames =
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
    };

    /// <summary>
    /// <paramref name="windowRect"/>가 <paramref name="monitorRect"/>를 완전히
    /// 뒤덮고, 데스크톱/셸 창이 아니고, 그 창이 펫과 같은 모니터에 있을
    /// 때만 true를 반환한다.
    ///
    /// <paramref name="isSameMonitorAsPet"/>이 false면 나머지 조건과
    /// 무관하게 항상 false다 — 다른 모니터를 뒤덮는 전체화면 앱은 펫이
    /// 있는 모니터를 전혀 가리지 않으므로 숨을 이유가 없다. 다중 모니터
    /// 환경에서 실측된 버그: 보조 모니터를 덮는 창(작업 표시줄이 없어
    /// 기하학적 검사를 항상 통과) 때문에 펫이 거의 항상 숨어버렸다.
    /// </summary>
    public static bool IsFullscreenApp(
        PixelRect windowRect,
        PixelRect monitorRect,
        string? windowClassName,
        bool isShellWindow,
        bool isSameMonitorAsPet)
    {
        if (!isSameMonitorAsPet) return false;

        if (isShellWindow) return false;

        if (windowClassName is not null)
        {
            foreach (var shellClassName in ShellWindowClassNames)
            {
                if (string.Equals(windowClassName, shellClassName, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
        }

        return windowRect.Left <= monitorRect.Left
            && windowRect.Top <= monitorRect.Top
            && windowRect.Right >= monitorRect.Right
            && windowRect.Bottom >= monitorRect.Bottom;
    }
}
