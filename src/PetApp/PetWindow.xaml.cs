using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using PetCore;

namespace PetApp;

public partial class PetWindow : Window
{
    private const int Fps = 12;                 // 픽셀아트에 60fps는 과하다
    private const double BandRatio = 0.15;      // 하단 15% 띠
    private const double PixelsPerTick = 3.0;
    private const int SleepAfterTicks = Fps * 20;   // 20초간 Idle이면 잠든다

    private readonly SpriteSheet _sheet = new();
    private readonly DispatcherTimer _timer;

    private int _frame;
    private int _idleTicks;
    private double _x;
    private int _direction = 1;
    private PetState _state = PetState.Idle;

    public PetWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(1000.0 / Fps)
        };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public void SetState(PetState state) => _state = state;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        NativeMethods.MakeNonInteractive(new WindowInteropHelper(this).Handle);

        var work = SystemParameters.WorkArea;
        _x = work.Left + work.Width / 2;
        Top = work.Bottom - Height - work.Height * BandRatio / 2;
    }

    private void Tick()
    {
        // 전체화면 앱이 앞에 있으면 숨고 렌더링을 멈춘다. 잠든 상태에서도
        // 이 검사만은 계속 돌아야 한다 — 그래야 잠든 채로 전체화면 앱이
        // 뜨더라도 펫이 곧바로 숨는다. 검사 자체는 P/Invoke 몇 번뿐이라
        // 12fps로 돌려도 비용은 무시할 수준이다.
        if (NativeMethods.IsFullscreenAppForeground())
        {
            if (Visibility == Visibility.Visible) Visibility = Visibility.Hidden;
            return;
        }
        if (Visibility != Visibility.Visible) Visibility = Visibility.Visible;

        var work = SystemParameters.WorkArea;

        if (_state == PetState.NeedsYou)
        {
            // 하단 중앙으로 모인다. 띠를 벗어나지 않는다.
            var center = work.Left + work.Width / 2 - Width / 2;
            _x += Math.Sign(center - _x) * PixelsPerTick;
        }
        else if (_state != PetState.Idle)
        {
            _x += _direction * PixelsPerTick;
            if (_x <= work.Left) { _x = work.Left; _direction = 1; }
            if (_x >= work.Right - Width) { _x = work.Right - Width; _direction = -1; }
        }

        Left = _x;
        Top = work.Bottom - Height - work.Height * BandRatio / 2;

        // 잠들면 렌더링을 멈춘다 (스펙 §6.5). Sprite.Source를 건드리지 않으면
        // WPF는 해당 프로퍼티가 바뀌지 않았다고 보고 Measure/Arrange/Render를
        // 다시 예약하지 않는다 — Left/Top도 이전과 같은 값이 재대입될 뿐이라
        // 마찬가지로 무효화를 일으키지 않는다. 즉 "잠들었다"는 것은 타이머가
        // 멈추는 게 아니라, 타이머는 돌아도 WPF의 레이아웃/렌더 파이프라인에는
        // 아무 일도 일어나지 않는다는 뜻이다. 타이머 자체를 멈추면 위의
        // 전체화면 감지도 함께 멈춰 "잠든 채로 전체화면 앱이 떠도 숨지 않는"
        // 회귀가 생기므로 의도적으로 타이머는 계속 둔다.
        _idleTicks = _state == PetState.Idle ? _idleTicks + 1 : 0;
        if (_idleTicks > SleepAfterTicks) return;

        _frame = (_frame + 1) % SpriteSheet.Columns;
        Sprite.Source = _sheet.Frame(_state, _frame);
    }
}
