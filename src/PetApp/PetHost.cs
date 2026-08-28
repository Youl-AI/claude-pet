using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;
using PetCore;

namespace PetApp;

/// <summary>
/// PetCore 조각들을 묶어 창에 상태를 공급한다.
/// 폴링 주기는 1초 — Claude Code 쪽에 비용을 전혀 만들지 않는다.
/// </summary>
internal sealed class PetHost
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(10);

    // 알림 파일 하나가 "지금 반응해도 의미 있는" 신선도의 상한.
    // idle_prompt는 60초 무응답 뒤에 발동하는 훅이므로 그보다 넉넉히 잡아
    // 폴링 지연·훅 비동기 실행 지연을 흡수하면서도, 펫이 며칠 꺼져 있다가
    // 켜졌을 때 쌓인 옛 permission_prompt를 "지금 막힘"으로 오인하지 않을
    // 만큼은 짧게 잡는다 (스펙 §8 "거짓 대기 신호" 금지와 같은 이유).
    private static readonly long StaleAfterMs = (long)TimeSpan.FromMinutes(2).TotalMilliseconds;

    /// <summary>
    /// 레벨 갱신 주기. 1Hz 폴링에 얹지 않는 이유는 매초 파일을 재스캔할 이유가 없기 때문이다.
    /// 레벨 하나가 오르는 데 가장 빠른 사용자도 3일이 걸리므로 30초 지연은 무의미하다 (스펙 §7.4).
    /// </summary>
    private const int LevelPollTicks = 30;

    private readonly UsageTracker _usage;
    private int _levelTickCounter;

    // 이전 Refresh 가 아직 백그라운드에서 도는 중이면 겹쳐 돌리지 않는다. 콜드 스캔은
    // 파일 수백 개·1GB 이상을 읽을 수 있어(UsageTracker 문서 참고) 30초 안에 안 끝날
    // 수도 있다. UI 스레드(이 카운터를 읽는 쪽)와 스레드 풀 태스크(이걸 false 로
    // 되돌리는 쪽)가 같이 건드리므로 volatile 로 가시성을 보장한다.
    private volatile bool _levelRefreshInFlight;

    private readonly PetWindow _window;
    private readonly string _dataDir;
    private readonly SessionRegistry _registry;
    private readonly Watchdog _watchdog;

    // 세션마다 독립된 상태를 유지한다. 하나로 공유하면 세션 A의 턴 종료가
    // 세션 B의 작업 중 상태를 덮어써서 거짓 대기 신호가 나온다 (스펙 §8).
    private readonly Dictionary<string, TranscriptTail> _tails = new();
    private readonly Dictionary<string, PetStateMachine> _machines = new();
    private readonly DispatcherTimer _timer;

    public PetHost(PetWindow window, string dataDir)
    {
        _window = window;
        _dataDir = dataDir;
        _registry = new SessionRegistry(Path.Combine(dataDir, "sessions"));
        _watchdog = new Watchdog(new WindowsProcessProbe(), Grace);

        // 트랜스크립트는 데이터 디렉터리가 아니라 Claude Code 의 프로젝트 디렉터리에 있다.
        var projectsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "projects");
        _usage = new UsageTracker(projectsRoot, new UsageStore(dataDir), new TranscriptCostScanner());

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += (_, _) => Poll();
    }

    public void Start() => _timer.Start();

    private void Poll()
    {
        // 장식용 펫이 어떤 이유로든 죽는 것보다는, 이번 주기를 통째로 건너뛰고
        // 다음 1초 폴링에서 다시 시도하는 편이 낫다. 아래 각 조각(SessionRegistry,
        // Watchdog/IProcessProbe, TranscriptTail)은 이미 "절대 던지지 않음"이
        // 계약이지만, 이 메서드는 그 계약들을 조합하는 지점이라 리플렉션 호출
        // 등 계약 밖의 코드도 섞여 있다. 구조적으로 한 번 더 감싼다.
        try
        {
            PollCore();
        }
        catch (Exception)
        {
            // 다음 폴링에서 다시 본다.
        }
    }

    private void PollCore()
    {
        var sessions = _registry.ReadAll();

        if (_watchdog.ShouldExit(sessions, DateTimeOffset.UtcNow))
        {
            _timer.Stop();
            System.Windows.Application.Current.Shutdown();
            return;
        }

        var liveIds = new HashSet<string>();

        foreach (var session in sessions)
        {
            liveIds.Add(session.SessionId);

            if (!_tails.TryGetValue(session.SessionId, out var tail))
            {
                tail = new TranscriptTail(session.TranscriptPath);
                tail.SkipToEnd();
                _tails[session.SessionId] = tail;
                _machines[session.SessionId] = new PetStateMachine();
            }

            var machine = _machines[session.SessionId];
            foreach (var e in tail.ReadNew())
                machine.Apply(e);
        }

        // 사라진 세션은 정리한다. 그러지 않으면 죽은 세션의 상태가 집계에 계속 남는다.
        foreach (var goneId in _tails.Keys.Where(id => !liveIds.Contains(id)).ToList())
        {
            _tails.Remove(goneId);
            _machines.Remove(goneId);
        }

        DrainNotifications();
        _window.SetState(PetStateMachine.Aggregate(_machines.Values));

        // 30초에 한 번만 레벨을 다시 센다. PollCore 는 DispatcherTimer.Tick 콜백이라
        // PetWindow 의 12fps 스프라이트 루프와 같은(유일한) UI 스레드에서 돈다
        // (App.xaml.cs: 이 앱은 STA 메인 스레드 하나뿐이고 별도 Dispatcher를 만들지
        // 않는다). Refresh 는 콜드 스캔 시 파일 수백 개·1GB 이상을 읽을 수 있으므로
        // (UsageTracker 문서 참고) 여기서 그대로 부르면 스캔하는 동안 애니메이션이
        // 눈에 띄게 멎는다. 그래서 스레드 풀로 넘긴다.
        //
        // Refresh 자체는 절대 던지지 않지만, 이걸 백그라운드로 넘기는 코드는 그 계약
        // 밖의 새 코드이므로 태스크 본문 전체를 catch(Exception)으로 감싼다 — 관찰되지
        // 않은 태스크 예외가 프로세스를 죽이는 일은 없어야 한다. 이전 Refresh 가 아직
        // 끝나지 않았으면 겹쳐 돌리지 않고 다음 주기로 미룬다(카운터를 리셋하지 않는다).
        if (_levelTickCounter <= 0 && !_levelRefreshInFlight)
        {
            _levelRefreshInFlight = true;
            _levelTickCounter = LevelPollTicks;

            Task.Run(() =>
            {
                try
                {
                    var snapshot = _usage.Refresh();
                    _window.SetLevel(snapshot.Level, snapshot.LeveledUp);
                }
                catch (Exception)
                {
                    // 다음 주기에 다시 시도한다.
                }
                finally
                {
                    _levelRefreshInFlight = false;
                }
            });
        }
        _levelTickCounter--;
    }

    private void DrainNotifications()
    {
        var notifyDir = Path.Combine(_dataDir, "notify");
        if (!Directory.Exists(notifyDir)) return;

        // 열거 자체를 try 안에 넣는다. Directory.EnumerateFiles 는 지연 평가라
        // MoveNext() 에서 던질 수 있고, foreach 를 try 밖에 두면 그 예외가 새어나간다.
        // (이 실수는 이 프로젝트에서 이미 SessionRegistry 에서 한 번 잡혔다.)
        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(notifyDir, "*.json").ToList();
        }
        catch (Exception)
        {
            return;   // 다음 주기에 다시 본다.
        }

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        foreach (var file in files)
        {
            try
            {
                // 신선도는 파일명(훅이 쓴 atUnixMs)과 지금 시각의 벽시계 차이로 판단한다.
                // 펫이 며칠 꺼져 있다 켜지면 notify/ 아래에 옛 알림이 쌓여 있을 수 있는데,
                // 그걸 지금 막 일어난 일처럼 세션에 적용하면 이미 끝난 permission_prompt가
                // "지금 막힘"으로 되살아나는 거짓 신호가 된다. 파일명을 못 읽거나 신선하지
                // 않으면 아예 파싱을 시도하지 않는다 — 그래야 영영 못 지우는 손상 파일도
                // (신선도 기준을 넘기는 순간부터는) 파싱 실패 없이 곧장 삭제 경로를 탄다.
                var isFresh = long.TryParse(Path.GetFileNameWithoutExtension(file), out var stamp)
                              && nowMs - stamp <= StaleAfterMs;

                if (isFresh)
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    var root = doc.RootElement;

                    // 알림을 해당 세션의 상태 머신으로 보낸다.
                    // 세션을 알 수 없으면 무시한다 — 엉뚱한 세션에 대기 신호를 붙이면 안 된다.
                    if (root.TryGetProperty("notificationType", out var type)
                        && root.TryGetProperty("sessionId", out var sid)
                        && sid.GetString() is { } sessionId
                        && _machines.TryGetValue(sessionId, out var machine))
                    {
                        machine.ApplyNotification(type.GetString() ?? "");
                    }
                }

                // 읽었든(신선해서 적용했든) 아니든(오래돼서 건너뛰었든) 소비한 파일은
                // 여기서 지운다 — 펫이 꺼져 있는 동안 쌓인 낡은 파일도 이렇게 결국 청소된다.
                File.Delete(file);
            }
            catch (Exception)
            {
                // 훅이 쓰는 중일 수 있다(신선한 파일인데 아직 다 안 써져서 파싱 실패).
                // 그런 파일은 지우지 않고 다음 주기에 다시 본다. catch-all은 의도적이다 —
                // 장식용 펫의 알림 처리가 실패했다고 해서 펫이 죽어서는 안 된다.
                // IOException·JsonException만 잡으면 UnauthorizedAccessException 같은 게
                // 새어나간다.
            }
        }
    }
}
