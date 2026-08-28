using PetCore;
using Xunit;

public class WatchdogTests
{
    private sealed class FakeProbe : IProcessProbe
    {
        public HashSet<int> Alive { get; } = new();
        public bool IsAlive(int pid, long startUnixMs) => Alive.Contains(pid);
    }

    private static SessionRecord Session(int pid) =>
        new($"s{pid}", "p", pid, 0, 0);

    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(10);

    [Fact]
    public void DoesNotExit_WhileAnySessionIsAlive()
    {
        var probe = new FakeProbe();
        probe.Alive.Add(1);
        var watchdog = new Watchdog(probe, Grace);

        Assert.False(watchdog.ShouldExit(new[] { Session(1), Session(2) }, T0));
    }

    [Fact]
    public void DoesNotExitImmediately_WhenAllSessionsDie()
    {
        // /clear 와 /compact 는 SessionEnd 직후 SessionStart 를 낸다.
        // 유예 시간이 없으면 펫이 깜빡인다.
        var watchdog = new Watchdog(new FakeProbe(), Grace);

        Assert.False(watchdog.ShouldExit(new[] { Session(1) }, T0));
    }

    [Fact]
    public void Exits_AfterGraceElapsesWithNoLiveSession()
    {
        var watchdog = new Watchdog(new FakeProbe(), Grace);

        Assert.False(watchdog.ShouldExit(new[] { Session(1) }, T0));
        Assert.True(watchdog.ShouldExit(new[] { Session(1) }, T0 + Grace + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void GraceResets_WhenASessionComesBackAlive()
    {
        var probe = new FakeProbe();
        var watchdog = new Watchdog(probe, Grace);

        watchdog.ShouldExit(new[] { Session(1) }, T0);          // 유예 시작
        probe.Alive.Add(1);                                      // 세션 부활 (/clear 후 재등록)
        watchdog.ShouldExit(new[] { Session(1) }, T0 + TimeSpan.FromSeconds(5));
        probe.Alive.Remove(1);

        var justAfterOriginalGrace = T0 + Grace + TimeSpan.FromSeconds(1);
        Assert.False(watchdog.ShouldExit(new[] { Session(1) }, justAfterOriginalGrace));

        // Verify the grace window correctly restarts when the session comes back and dies again.
        // Without this check, a buggy implementation that re-anchors _emptySince (sets _emptySince = now
        // instead of _emptySince = null on the alive branch) would cause premature exit around
        // T0+5s+Grace+1s=T0+16s. The correct implementation (clearing to null) sets _emptySince=T0+11s
        // on the next check, so it should stay alive until T0+11s+Grace+1s=T0+22s.
        var revivalDeathTime = T0 + TimeSpan.FromSeconds(5);
        var incorrectPrematureDeadline = revivalDeathTime + Grace + TimeSpan.FromSeconds(1);
        Assert.False(watchdog.ShouldExit(new[] { Session(1) }, incorrectPrematureDeadline));
    }

    [Fact]
    public void Exits_AfterGrace_WhenRegistryIsEmpty()
    {
        var watchdog = new Watchdog(new FakeProbe(), Grace);

        Assert.False(watchdog.ShouldExit(Array.Empty<SessionRecord>(), T0));
        Assert.True(watchdog.ShouldExit(Array.Empty<SessionRecord>(), T0 + Grace + TimeSpan.FromSeconds(1)));
    }
}
