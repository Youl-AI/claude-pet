using PetCore;
using Xunit;

public class PetStateMachineTests
{
    private static TranscriptEvent Tool(string name) =>
        new(TranscriptEventKind.ToolUse, name);

    [Theory]
    [InlineData("Read", PetState.Reading)]
    [InlineData("Grep", PetState.Reading)]
    [InlineData("Glob", PetState.Reading)]
    [InlineData("WebFetch", PetState.Reading)]
    [InlineData("Edit", PetState.Writing)]
    [InlineData("Write", PetState.Writing)]
    [InlineData("NotebookEdit", PetState.Writing)]
    [InlineData("Bash", PetState.Running)]
    [InlineData("PowerShell", PetState.Running)]
    public void MapsToolNameToState(string tool, PetState expected)
    {
        var machine = new PetStateMachine();
        machine.Apply(Tool(tool));
        Assert.Equal(expected, machine.Current);
    }

    [Fact]
    public void UnknownTool_FallsBackToRunning()
    {
        var machine = new PetStateMachine();
        machine.Apply(Tool("SomeFutureTool"));
        Assert.Equal(PetState.Running, machine.Current);
    }

    [Fact]
    public void ErrorResult_EntersErrorState()
    {
        var machine = new PetStateMachine();
        machine.Apply(Tool("Bash"));
        machine.Apply(new TranscriptEvent(TranscriptEventKind.ToolResult, null, IsError: true));
        Assert.Equal(PetState.Error, machine.Current);
    }

    [Fact]
    public void AssistantTextWithNoPendingTool_MeansTurnEnded()
    {
        var machine = new PetStateMachine();
        machine.Apply(Tool("Read"));
        machine.Apply(new TranscriptEvent(TranscriptEventKind.ToolResult));
        machine.Apply(new TranscriptEvent(TranscriptEventKind.AssistantText));

        Assert.Equal(PetState.NeedsYou, machine.Current);
        Assert.Equal(NeedsYouLevel.YourTurn, machine.NeedsYou);
    }

    [Fact]
    public void AssistantTextFollowedByToolUse_IsNotTurnEnd()
    {
        // 제가 설명을 쓰고 이어서 도구를 호출하는 흔한 경우.
        var machine = new PetStateMachine();
        machine.Apply(new TranscriptEvent(TranscriptEventKind.AssistantText));
        machine.Apply(Tool("Read"));

        Assert.Equal(PetState.Reading, machine.Current);
        Assert.Equal(NeedsYouLevel.None, machine.NeedsYou);
    }

    [Fact]
    public void PermissionPromptNotification_EscalatesToBlocked()
    {
        var machine = new PetStateMachine();
        machine.ApplyNotification("permission_prompt");

        Assert.Equal(PetState.NeedsYou, machine.Current);
        Assert.Equal(NeedsYouLevel.Blocked, machine.NeedsYou);
    }

    [Fact]
    public void IdlePromptNotification_EscalatesToAbandoned()
    {
        var machine = new PetStateMachine();
        machine.ApplyNotification("idle_prompt");
        Assert.Equal(NeedsYouLevel.Abandoned, machine.NeedsYou);
    }

    [Fact]
    public void NewToolUse_ClearsNeedsYou()
    {
        var machine = new PetStateMachine();
        machine.ApplyNotification("idle_prompt");
        machine.Apply(Tool("Read"));

        Assert.Equal(PetState.Reading, machine.Current);
        Assert.Equal(NeedsYouLevel.None, machine.NeedsYou);
    }

    [Fact]
    public void StartsIdle()
    {
        Assert.Equal(PetState.Idle, new PetStateMachine().Current);
    }

    // --- 여러 세션 집계 (스펙 §8) ---

    [Fact]
    public void Aggregate_ShowsWorkingSession_WhenAnotherSessionEndedItsTurn()
    {
        // 세션 A는 내 차례로 끝났고 세션 B는 일하는 중이다.
        // 펫은 일하는 쪽을 연기해야 한다 — 대기 신호를 내면 거짓말이 된다.
        var idleSession = new PetStateMachine();
        idleSession.Apply(new TranscriptEvent(TranscriptEventKind.AssistantText));

        var busySession = new PetStateMachine();
        busySession.Apply(Tool("Write"));

        Assert.Equal(PetState.Writing,
            PetStateMachine.Aggregate(new[] { idleSession, busySession }));
    }

    [Fact]
    public void Aggregate_ShowsNeedsYou_OnlyWhenEverySessionIsWaiting()
    {
        var a = new PetStateMachine();
        a.Apply(new TranscriptEvent(TranscriptEventKind.AssistantText));
        var b = new PetStateMachine();
        b.ApplyNotification("permission_prompt");

        Assert.Equal(PetState.NeedsYou, PetStateMachine.Aggregate(new[] { a, b }));
    }

    [Fact]
    public void Aggregate_PrefersMostRecentlyActiveSession()
    {
        var older = new PetStateMachine();
        older.Apply(Tool("Read"));

        var newer = new PetStateMachine();
        newer.Apply(Tool("Bash"));   // 나중에 적용됨 → Sequence 가 더 큼

        Assert.Equal(PetState.Running,
            PetStateMachine.Aggregate(new[] { older, newer }));
    }

    [Fact]
    public void Aggregate_ReturnsIdle_WhenThereAreNoSessions()
    {
        Assert.Equal(PetState.Idle,
            PetStateMachine.Aggregate(Array.Empty<PetStateMachine>()));
    }
}
