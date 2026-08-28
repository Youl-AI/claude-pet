using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using PetCore;

namespace PetApp;

public partial class PetWindow : Window
{
    private const int Fps = 12;                 // 픽셀아트에 60fps는 과하다

    // 상태별 가로 이동 속도 (px/tick). 상태마다 별도 상수를 두어 의도가
    // 이름만 봐도 드러나게 한다 — 설계서 §9.1: Idle은 천천히 배회하고,
    // Running은 빠르게 걷는다.
    private const double IdleWanderPixelsPerTick = 1.0;
    private const double RunningPixelsPerTick = 3.0;
    private const double BlockedPixelsPerTick = 3.0;  // 하단 중앙으로 모이는 속도

    private const int SleepAfterTicks = Fps * 20;   // 20초간 Idle이면 잠든다

    // 전체화면 감지는 매 틱이 아니라 대략 초당 1회만 폴링한다. 최악의 경우
    // 숨김 전환이 최대 1초 늦어질 수 있지만, 이 카운터는 항상 계속 돌기
    // 때문에 "다시 보이지 않는" 회귀는 없다 — 다음 폴링에서 반드시 갱신된다.
    private const int FullscreenPollIntervalTicks = Fps;

    // 레이어드 윈도우(AllowsTransparency)는 위치가 바뀔 때마다 전체 서피스를
    // 재합성한다. 픽셀아트는 12fps 스프라이트 애니메이션과 달리 6Hz로만
    // 옮겨도 눈에는 그대로 매끄럽다. 그래서 위치 이동은 두 틱에 한 번만
    // 계산하고, 그 대신 한 번에 두 배 거리를 움직여 초당 이동 거리(체감
    // 속도)는 그대로 유지한다.
    private const double RepositionPixelMultiplier = 2.0;

    private readonly SpriteSheet _sheet = new();
    private readonly DispatcherTimer _timer;

    private int _frame;
    private int _idleTicks;
    private double _x;
    private int _direction = 1;
    private PetState _state = PetState.Idle;
    private int _repositionTick;

    private int _fullscreenPollCounter;
    private bool _isFullscreenHiding;
    private IntPtr _hwnd;

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
        // 창 핸들은 여기서만 얻을 수 있다(SourceInitialized 이전엔 없다) —
        // 매 틱 다시 조회하지 않도록 캐싱해 둔다. 전체화면 판정에서 펫
        // 자신의 모니터를 구할 때 이 핸들이 필요하다.
        _hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.MakeNonInteractive(_hwnd);

        var work = SystemParameters.WorkArea;
        _x = work.Left + work.Width / 2;

        // 발이 작업 영역 바닥에 오도록 배치한다 — 이것이 화면에서 가장 낮은,
        // 즉 하단 15% 띠 안에 가장 확실히 들어오는 위치다. 띠 높이가 창
        // 높이보다 작을 만큼 화면이 낮은 경우에도 이게 달성 가능한 최선이다.
        Top = work.Bottom - Height;
    }

    private void Tick()
    {
        // 전체화면 앱이 앞에 있으면 숨고 렌더링을 멈춘다. 잠든 상태에서도
        // 이 검사만은 계속 돌아야 한다 — 그래야 잠든 채로 전체화면 앱이
        // 뜨더라도 펫이 곧바로 숨는다. 다만 검사 자체(P/Invoke 몇 번)를 매
        // 틱 반복할 필요는 없으므로 대략 초당 1회로만 폴링한다.
        if (_fullscreenPollCounter <= 0)
        {
            _isFullscreenHiding = NativeMethods.IsFullscreenAppForeground(_hwnd);
            _fullscreenPollCounter = FullscreenPollIntervalTicks;
        }
        _fullscreenPollCounter--;

        if (_isFullscreenHiding)
        {
            if (Visibility == Visibility.Visible) Visibility = Visibility.Hidden;
            return;
        }
        if (Visibility != Visibility.Visible) Visibility = Visibility.Visible;

        // 잠들었는지 여부는 Left/Top/Sprite.Source를 조금이라도 건드리기
        // 전에 확정한다 — 그래야 잠든 펫은 위치 계산도, 창 재배치도, 스프라
        // 이트 갱신도 전혀 하지 않는다(레이어드 윈도우 재합성 0회). 상태가
        // Idle이 아닌 다른 값으로 바뀌면 이 카운터가 같은 틱에서 즉시 0으로
        // 리셋되므로 깨어남에는 지연이 없다. 위의 전체화면 검사만은 이
        // 리턴보다 앞에 있어 잠든 동안에도 계속 돌고, 잠든 채로 전체화면
        // 앱이 뜨거나 내려가도 숨김/재표시가 정상 동작한다.
        // Abandoned 도 잠듦 대상에 넣는다. 60초 방치 알림이 떴다는 것은 사람이
        // 자리에 없다는 뜻이므로, 그 상태에서 12fps 렌더 루프를 계속 돌릴 이유가
        // 없다. 누운 포즈는 정지 그림이라 멈춰도 보이는 것이 달라지지 않는다.
        var resting = _state == PetState.Idle || _state == PetState.Abandoned;
        _idleTicks = resting ? _idleTicks + 1 : 0;
        if (_idleTicks > SleepAfterTicks) return;

        var work = SystemParameters.WorkArea;

        // 레이어드 윈도우 재합성 비용을 줄이려고 위치 이동 자체는 6Hz로만
        // 계산한다(12fps 틱의 절반). 건너뛴 틱에는 _x가 그대로이므로 아래
        // Left 대입 가드가 자연히 걸러 SetWindowPos를 추가로 부르지 않는다.
        var shouldReposition = _repositionTick == 0;
        _repositionTick = (_repositionTick + 1) % 2;

        if (shouldReposition)
        {
            // 설계서 §9.1: Idle은 천천히 배회하고 가끔 앉거나 존다. Running은
            // 빠르게 걷는다. NeedsYou는 하단 중앙으로 모인다. Reading/Writing/
            // Error는 제자리에서 반응만 한다 — 가로 이동이 없다. 계산은 두
            // 틱에 한 번만 도니, 체감 속도를 유지하려고 틱당 거리를 두 배로
            // 준다.
            switch (_state)
            {
                case PetState.Blocked:
                    // 승인 대기만 화면 하단 중앙으로 모인다. 사람이 반드시 봐야
                    // 하는 유일한 상태이기 때문이다. YourTurn 과 Abandoned 는
                    // 제자리에 머문다 — 전자는 물음표로, 후자는 누운 실루엣으로
                    // 이미 구분되고, 굳이 시선을 끌 만큼 급하지 않다.
                    var center = work.Left + work.Width / 2 - Width / 2;
                    _x += Math.Sign(center - _x) * BlockedPixelsPerTick * RepositionPixelMultiplier;
                    break;
                case PetState.Idle:
                    Bounce(work, IdleWanderPixelsPerTick * RepositionPixelMultiplier);
                    break;
                case PetState.Running:
                    Bounce(work, RunningPixelsPerTick * RepositionPixelMultiplier);
                    break;
                default:
                    // Reading, Writing, Error: 제자리. _x를 건드리지 않는다 —
                    // 이러면 Left 재대입도 없어져 매 틱 SetWindowPos와 레이어드
                    // 서피스 재합성이 그만큼 줄어든다.
                    break;
            }
        }

        // 작업 영역이 줄어들 수 있다 (모니터 분리, 해상도 변경, 작업표시줄
        // 이동). 정지 상태에서도 stale한 _x가 새 작업 영역 밖에 남지 않도록
        // 상태와 무관하게 매 틱 clamp한다.
        _x = Math.Clamp(_x, work.Left, work.Right - Width);

        // 발이 작업 영역 바닥에 오는 배치를 유지한다 — 창 높이가 15% 띠
        // 높이보다 큰 저해상도(예: 1366x768)에서도 이게 항상 띠 안에 드는
        // 가장 낮은 위치다. (아래 리포트에 1366x768 산술 근거를 남긴다.)
        var top = work.Bottom - Height;

        // 값이 실제로 바뀔 때만 대입한다. WPF DP는 동일 값 대입을 내부적으로
        // 걸러내지만, 명시적으로도 걸러 Left/Top 관련 SetWindowPos 호출을
        // 확실히 줄인다. Left는 건너뛴 틱에는 _x가 안 바뀌므로 이 가드만으로
        // 자연히 6Hz로 눌린다 — shouldReposition을 따로 검사할 필요가 없다.
        if (Left != _x) Left = _x;
        if (Top != top) Top = top;

        _frame = (_frame + 1) % SpriteSheet.Columns;
        var next = _sheet.Frame(_state, _frame);

        // 시트 안에는 열이 달라도 그림이 같은 프레임이 섞여 있다
        // (SpriteSheet가 생성자에서 픽셀 동일 프레임을 같은 인스턴스로
        // 캐시한다). 실제로 그림이 바뀔 때만 Source를 재대입해 불필요한
        // 레이어드 윈도우 재합성을 피한다.
        if (!ReferenceEquals(next, Sprite.Source))
        {
            Sprite.Source = next;
        }
    }

    private void Bounce(Rect work, double pixelsPerTick)
    {
        _x += _direction * pixelsPerTick;
        if (_x <= work.Left) { _x = work.Left; _direction = 1; }
        if (_x >= work.Right - Width) { _x = work.Right - Width; _direction = -1; }
    }
}
