using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
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

    // --- 레벨 표시 ---
    private const double PixelScale = 2.0;          // 스프라이트 1px = 화면 2px
    private const int PetCellOriginX = 48;          // Canvas 안에서 펫이 시작하는 x (화면 px)
    private const int PetBodyLeftPx = 4;            // 스프라이트 좌표계에서 몸의 왼쪽 첫 픽셀

    private const int FlashFrames = 8;
    private static readonly Uri FlashUri = new("pack://application:,,,/assets/flash.png", UriKind.Absolute);

    private int _level;
    private BitmapSource[]? _flashFrames;
    private int _flashFrame = -1;                   // -1 = 재생 중 아님

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

        // 이펙트 프레임을 미리 잘라 얼려 둔다. 재생 중에 자르면 12fps 루프에서 할당이 생긴다.
        _flashFrames = LoadFlashFrames();
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

        // 레벨업 이펙트는 상태와 무관한 자기 타임라인으로 돈다. 잠듦 반환보다 앞에 있어야
        // 잠든 채로 레벨이 올라도 재생된다.
        AdvanceFlash();

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

    /// <summary>
    /// 레벨을 갱신한다. leveledUp 이면 이펙트를 한 번 재생한다.
    /// PetHost 가 30초 주기로 부른다 — 렌더 스레드에서 파일을 읽지 않기 위해서다.
    /// </summary>
    public void SetLevel(int level, bool leveledUp)
    {
        // 백그라운드 폴링 스레드에서 호출될 수 있다. WPF 요소는 만든 스레드에서만
        // 건드릴 수 있으므로, UI 스레드가 아니면 큐에 넣고 즉시 돌아온다 —
        // 호출자를 막지 않고, 렌더 스레드 밖에서 요소를 건드리지도 않는다.
        if (!Dispatcher.CheckAccess())
        {
            // 디스패처가 셧다운을 시작했거나 이미 끝났으면 그릴 대상이 없으므로
            // 갱신을 조용히 버린다. 이 검사와 BeginInvoke 사이에도 셧다운이 끼어들
            // 수 있는 경쟁 상태가 남기 때문에, 검사만으로는 불충분하고 호출 자체도
            // 예외로부터 보호해야 한다 — 백그라운드 스레드의 미처리 예외는 프로세스를
            // 종료시킨다.
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return;

            try
            {
                Dispatcher.BeginInvoke(new Action(() => SetLevel(level, leveledUp)));
            }
            catch (Exception)
            {
                // 위 검사 직후 셧다운이 시작된 경우의 경쟁 상태. 갱신을 버린다.
                // 종류를 열거하지 않는다 — 이 저장소는 catch(IOException) 만 잡았다가
                // UnauthorizedAccessException 에 세 번 뚫린 이력이 있다.
            }
            return;
        }

        // 여기부터는 UI 스레드에서 실제로 요소를 건드리는 부분이다. App.xaml.cs 에는
        // DispatcherUnhandledException 핸들러가 없으므로, 여기서 뭐가 되었든 던지면
        // 유일한 UI 스레드의 미처리 예외가 되어 프로세스 전체가 죽는다. 명패 렌더링이나
        // 레벨 표시 하나 실패했다고 펫 전체가 죽어서는 안 되므로 통째로 방어한다.
        // 종류를 열거하지 않는다 — catch(IOException) 만 잡았다가 다른 예외에 뚫린
        // 이력이 이 저장소에 있다.
        try
        {
            if (level != _level)
            {
                _level = level;
                Plate.Source = PlateRenderer.Render(level);

                // 명패는 펫 왼쪽에, 간격 GapPx 를 두고 붙는다. 몸의 왼쪽 첫 픽셀이 셀 안에서
                // PetBodyLeftPx 이므로 그만큼 더해서 판의 오른쪽 끝을 잡는다.
                var plateWidthPx = PlateRenderer.PlateWidthFor(level) * PixelScale;
                var petBodyLeftPx = PetCellOriginX + PetBodyLeftPx * PixelScale;
                Canvas.SetLeft(Plate, petBodyLeftPx - PlateRenderer.GapPx * PixelScale - plateWidthPx);

                Plate.Width = plateWidthPx;
                Plate.Height = PlateRenderer.PlateHeight * PixelScale;
                // 판의 세로 중심을 몸통 눈높이에 맞춘다 (스프라이트 y 15..23 구간).
                Canvas.SetTop(Plate, 15 * PixelScale);
            }

            if (leveledUp && _flashFrames is not null)
                _flashFrame = 0;
        }
        catch (Exception)
        {
            // 명패/이펙트 갱신 실패는 무시한다. 펫 애니메이션은 계속 돈다.
        }
    }

    private static BitmapSource[]? LoadFlashFrames()
    {
        // 리소스가 없거나 크기가 다르면 이펙트만 포기한다. 펫은 계속 돈다.
        try
        {
            var sheet = new BitmapImage();
            sheet.BeginInit();
            sheet.CacheOption = BitmapCacheOption.OnLoad;
            sheet.UriSource = FlashUri;
            sheet.EndInit();
            sheet.Freeze();

            if (sheet.PixelHeight != SpriteSheet.FrameSize
                || sheet.PixelWidth != SpriteSheet.FrameSize * FlashFrames)
                return null;

            var frames = new BitmapSource[FlashFrames];
            for (var i = 0; i < FlashFrames; i++)
            {
                var crop = new CroppedBitmap(sheet,
                    new Int32Rect(i * SpriteSheet.FrameSize, 0,
                                  SpriteSheet.FrameSize, SpriteSheet.FrameSize));
                crop.Freeze();
                frames[i] = crop;
            }
            return frames;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>이펙트를 한 프레임 진행한다. Tick 에서 부른다.</summary>
    private void AdvanceFlash()
    {
        if (_flashFrame < 0 || _flashFrames is null)
        {
            if (Flash.Visibility != Visibility.Collapsed) Flash.Visibility = Visibility.Collapsed;
            return;
        }

        Flash.Source = _flashFrames[_flashFrame];
        if (Flash.Visibility != Visibility.Visible) Flash.Visibility = Visibility.Visible;

        _flashFrame++;
        if (_flashFrame >= FlashFrames)
        {
            _flashFrame = -1;
            Flash.Visibility = Visibility.Collapsed;
        }
    }
}
