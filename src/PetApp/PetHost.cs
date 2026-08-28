using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
