using PetCore;

public class SleepGateTests
{
    private const long T0 = 1_000_000_000_000;
    private static TranscriptEvent RateLimited(long? resetAt) =>
        new(TranscriptEventKind.RateLimited, ResetAtUnixMs: resetAt);

    [Fact]
    public void StartsAwake()
    {
        Assert.False(new SleepGate().IsSleeping(T0));
    }

    [Fact]
    public void RateLimitedPutsItToSleep()
    {
        var gate = new SleepGate();
        gate.Observe(RateLimited(T0 + 60_000), T0);
        Assert.True(gate.IsSleeping(T0));
    }

    [Fact]
    public void WakesWhenTheResetInstantPasses()
    {
        var gate = new SleepGate();
        gate.Observe(RateLimited(T0 + 60_000), T0);

        Assert.True(gate.IsSleeping(T0 + 59_999));
        Assert.False(gate.IsSleeping(T0 + 60_000));   // 경계 포함: 시각이 되면 깬다
    }

    [Fact]
    public void AStaleRateLimitLineDoesNotCauseAGhostNap()
    {
        // 리셋 시각이 이미 지난 과거 줄 — 잠들자마자 다음 검사에서 깬다 (스펙 §2).
        var gate = new SleepGate();
        gate.Observe(RateLimited(T0 - 1), T0);
        Assert.False(gate.IsSleeping(T0));
    }

    [Fact]
    public void QuotaResumedWakesImmediately()
    {
        var gate = new SleepGate();
        gate.Observe(RateLimited(T0 + 3_600_000), T0);
        gate.OnQuotaResumed();
        Assert.False(gate.IsSleeping(T0));
    }

    [Fact]
    public void FreshToolActivityWakes()
    {
        var gate = new SleepGate();
        gate.Observe(RateLimited(null), T0);          // 시각 모름 (월 지출 한도)
        Assert.True(gate.IsSleeping(T0));

        gate.Observe(new TranscriptEvent(TranscriptEventKind.ToolUse, "Read"), T0 + 1_000);
        Assert.False(gate.IsSleeping(T0 + 1_000));
    }

    [Fact]
    public void SuccessfulToolResultWakes()
    {
        var gate = new SleepGate();
        gate.Observe(RateLimited(null), T0);
        gate.Observe(new TranscriptEvent(TranscriptEventKind.ToolResult, IsError: false), T0 + 1_000);
        Assert.False(gate.IsSleeping(T0 + 1_000));
    }

    [Fact]
    public void AssistantTextAndErrorsDoNotWake()
    {
        var gate = new SleepGate();
        gate.Observe(RateLimited(null), T0);

        gate.Observe(new TranscriptEvent(TranscriptEventKind.AssistantText), T0 + 1_000);
        gate.Observe(new TranscriptEvent(TranscriptEventKind.ToolResult, IsError: true), T0 + 1_000);
        gate.Observe(new TranscriptEvent(TranscriptEventKind.Thinking), T0 + 1_000);
        Assert.True(gate.IsSleeping(T0 + 1_000));
    }

    [Fact]
    public void ANewRateLimitWhileAsleepExtendsTheNap()
    {
        var gate = new SleepGate();
        gate.Observe(RateLimited(T0 + 60_000), T0);
        gate.Observe(RateLimited(T0 + 120_000), T0 + 30_000);   // 더 늦은 리셋으로 갱신
        Assert.True(gate.IsSleeping(T0 + 90_000));
        Assert.False(gate.IsSleeping(T0 + 120_000));
    }
}
