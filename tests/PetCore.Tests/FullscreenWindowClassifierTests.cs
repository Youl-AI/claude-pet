using PetCore;
using Xunit;

public class FullscreenWindowClassifierTests
{
    // 이 머신에서 실측한 값 그대로 — Progman(바탕화면)의 사각형은 가상
    // 데스크톱 전체를 덮으므로 단일 모니터를 항상 뒤덮는다. 기하학적
    // 검사만으로는 이걸 "전체화면 앱"으로 오판했던 것이 실제 버그다.
    private static readonly PixelRect ProgmanMeasuredRect = new(-1920, 0, 1920, 1080);
    private static readonly PixelRect MonitorRect = new(0, 0, 1920, 1080);

    [Fact]
    public void Progman_CoveringMonitor_IsNotFullscreen()
    {
        var result = FullscreenWindowClassifier.IsFullscreenApp(
            ProgmanMeasuredRect, MonitorRect, "Progman", isShellWindow: false, isSameMonitorAsPet: true);

        Assert.False(result);
    }

    [Fact]
    public void WorkerW_CoveringMonitor_IsNotFullscreen()
    {
        // 현재 이 머신에서 WorkerW는 작지만(136x39), 바탕화면 호스팅 여부는
        // 벽지 설정에 따라 달라져 언제든 모니터 전체 크기가 될 수 있다.
        // 그러니 지금 크기가 아니라 모니터를 뒤덮는 가상의 크기로 검증한다.
        var result = FullscreenWindowClassifier.IsFullscreenApp(
            MonitorRect, MonitorRect, "WorkerW", isShellWindow: false, isSameMonitorAsPet: true);

        Assert.False(result);
    }

    [Fact]
    public void ShellTrayWnd_CoveringMonitor_IsNotFullscreen()
    {
        var result = FullscreenWindowClassifier.IsFullscreenApp(
            MonitorRect, MonitorRect, "Shell_TrayWnd", isShellWindow: false, isSameMonitorAsPet: true);

        Assert.False(result);
    }

    [Fact]
    public void ShellSecondaryTrayWnd_CoveringMonitor_IsNotFullscreen()
    {
        var result = FullscreenWindowClassifier.IsFullscreenApp(
            MonitorRect, MonitorRect, "Shell_SecondaryTrayWnd", isShellWindow: false, isSameMonitorAsPet: true);

        Assert.False(result);
    }

    [Fact]
    public void ShellWindowHandle_IsNotFullscreen_EvenWithUnrelatedClassName()
    {
        // GetShellWindow()와 일치하는 창은 클래스 이름이 무엇이든 데스크톱이다.
        var result = FullscreenWindowClassifier.IsFullscreenApp(
            MonitorRect, MonitorRect, "SomeArbitraryClassName", isShellWindow: true, isSameMonitorAsPet: true);

        Assert.False(result);
    }

    [Fact]
    public void OrdinaryWindow_ExactlyCoveringMonitor_IsFullscreen()
    {
        var result = FullscreenWindowClassifier.IsFullscreenApp(
            MonitorRect, MonitorRect, "Chrome_WidgetWin_1", isShellWindow: false, isSameMonitorAsPet: true);

        Assert.True(result);
    }

    [Fact]
    public void OrdinaryWindow_SpanningMoreThanMonitor_IsFullscreen()
    {
        // 여러 모니터에 걸친 전체화면 앱(예: 확장된 프레젠테이션)도 여전히
        // 전체화면으로 간주해야 한다 — 기존 기하학적 검사의 동작을 그대로
        // 유지한다.
        var spanningRect = new PixelRect(-1920, 0, 1920, 1080);

        var result = FullscreenWindowClassifier.IsFullscreenApp(
            spanningRect, MonitorRect, "Chrome_WidgetWin_1", isShellWindow: false, isSameMonitorAsPet: true);

        Assert.True(result);
    }

    [Fact]
    public void OrdinaryMaximizedWindow_StoppingAtWorkArea_IsNotFullscreen()
    {
        // 작업 표시줄 높이(48px)만큼 작업 영역이 모니터보다 낮게 끝난다 —
        // 일반적인 "최대화"는 여기서 멈추므로 전체화면이 아니다.
        var workAreaRect = new PixelRect(0, 0, 1920, 1032);

        var result = FullscreenWindowClassifier.IsFullscreenApp(
            workAreaRect, MonitorRect, "Chrome_WidgetWin_1", isShellWindow: false, isSameMonitorAsPet: true);

        Assert.False(result);
    }

    [Theory]
    [InlineData("progman")]
    [InlineData("PROGMAN")]
    [InlineData("PrOgMaN")]
    public void ClassNameComparison_IsCaseInsensitive(string className)
    {
        var result = FullscreenWindowClassifier.IsFullscreenApp(
            MonitorRect, MonitorRect, className, isShellWindow: false, isSameMonitorAsPet: true);

        Assert.False(result);
    }

    // 사용자 실측값 그대로: Chrome 창이 두 번째 모니터(-1920,0)-(0,1080)를
    // 정확히 뒤덮고, 펫은 첫 번째(주) 모니터(0,0)-(1920,1080)에 있다. 기존
    // 기하학적 검사만으로는 이걸 "전체화면"으로 오판해 펫이 숨어버렸다 —
    // 재현된 실제 버그.
    private static readonly PixelRect SecondMonitorRect = new(-1920, 0, 0, 1080);

    [Fact]
    public void FullscreenOnDifferentMonitorFromPet_IsNotFullscreen()
    {
        var result = FullscreenWindowClassifier.IsFullscreenApp(
            SecondMonitorRect, SecondMonitorRect, "Chrome_WidgetWin_1",
            isShellWindow: false, isSameMonitorAsPet: false);

        Assert.False(result);
    }

    [Fact]
    public void FullscreenOnSameMonitorAsPet_IsFullscreen()
    {
        var result = FullscreenWindowClassifier.IsFullscreenApp(
            SecondMonitorRect, SecondMonitorRect, "Chrome_WidgetWin_1",
            isShellWindow: false, isSameMonitorAsPet: true);

        Assert.True(result);
    }
}
