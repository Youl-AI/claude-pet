namespace PetCore;

/// <summary>
/// 펫의 종료 시점을 판정한다.
/// SessionEnd 훅은 신뢰할 수 없으므로(크래시·터미널 강제 종료 시 미발동,
/// /clear·/compact 시 오발동) PID 생존이 권위다.
/// </summary>
public sealed class Watchdog
{
    private readonly IProcessProbe _probe;
    private readonly TimeSpan _grace;
    private DateTimeOffset? _emptySince;

    public Watchdog(IProcessProbe probe, TimeSpan grace)
    {
        _probe = probe;
        _grace = grace;
    }

    public bool ShouldExit(IReadOnlyList<SessionRecord> sessions, DateTimeOffset now)
    {
        var anyAlive = sessions.Any(s => _probe.IsAlive(s.Pid, s.PidStartUnixMs));

        if (anyAlive)
        {
            _emptySince = null;
            return false;
        }

        _emptySince ??= now;
        return now - _emptySince.Value > _grace;
    }
}
