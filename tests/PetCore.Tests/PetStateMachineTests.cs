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

        Assert.Equal(PetState.YourTurn, machine.Current);
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

        Assert.Equal(PetState.Blocked, machine.Current);
        Assert.Equal(NeedsYouLevel.Blocked, machine.NeedsYou);
    }

    [Fact]
    public void IdlePromptNotification_EscalatesToAbandoned()
    {
        var machine = new PetStateMachine();
        machine.ApplyNotification("idle_prompt");
        Assert.Equal(NeedsYouLevel.Abandoned, machine.NeedsYou);
        Assert.Equal(PetState.Abandoned, machine.Current);
    }

    [Fact]
    public void SuccessfulToolResult_AfterPermissionPrompt_ClearsNeedsYou()
    {
        // permission_prompt 로 막힌 뒤 사용자가 승인하면 도구가 실행된다.
        // 성공 결과가 오면 더 이상 "사람이 필요"하지 않다 — 일이 진행 중이다.
        var machine = new PetStateMachine();
        machine.Apply(Tool("Bash"));
        machine.ApplyNotification("permission_prompt");
        machine.Apply(new TranscriptEvent(TranscriptEventKind.ToolResult));

        Assert.Equal(NeedsYouLevel.None, machine.NeedsYou);
        Assert.Equal(PetState.Idle, machine.Current);
    }

    [Fact]
    public void SuccessfulToolResult_ClearsErrorFromEarlierToolInSameTurn()
    {
        // 한 턴에 도구 두 개가 호출되고(A, B), A의 결과가 에러였다가
        // 이어서 B의 결과가 성공으로 온다. 성공 결과는 남아있던 에러를 지워야 한다.
        var machine = new PetStateMachine();
        machine.Apply(Tool("Bash"));
        machine.Apply(Tool("Read"));
        machine.Apply(new TranscriptEvent(TranscriptEventKind.ToolResult, null, IsError: true));
        Assert.Equal(PetState.Error, machine.Current);

        machine.Apply(new TranscriptEvent(TranscriptEventKind.ToolResult));

        Assert.NotEqual(PetState.Error, machine.Current);
        Assert.Equal(PetState.Idle, machine.Current);
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
    public void Aggregate_PrefersBlockedSession_OverAnyOtherSignal()
    {
        var a = new PetStateMachine();
        a.Apply(new TranscriptEvent(TranscriptEventKind.AssistantText));
        var b = new PetStateMachine();
        b.ApplyNotification("permission_prompt");

        Assert.Equal(PetState.Blocked, PetStateMachine.Aggregate(new[] { a, b }));
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

    [Fact]
    public void Aggregate_ShowsTheMostRecentEvent_EvenIfItIsAWaitingSignal()
    {
        // 의도적인 동작 변경이다. 예전 Aggregate 는 대기 세션을 최신순 정렬
        // *전에* 걸러내서, 오래된 작업 세션이 방금 막 대기 상태가 된 세션을
        // 언제나 이겼다. 그 규칙이 "창 두 개를 열면 신호가 사라지는" 버그의
        // 절반이었다. 이제는 가장 최근에 일어난 일이 이긴다 — 실제로 작업
        // 중인 세션은 이벤트를 계속 만들어 내므로 자연히 Sequence 가 높다.
        var busySession = new PetStateMachine();
        busySession.Apply(Tool("Write"));                                            // 먼저 → Sequence 낮음

        var waitingSession = new PetStateMachine();
        waitingSession.Apply(new TranscriptEvent(TranscriptEventKind.AssistantText)); // 나중 → Sequence 높음

        Assert.Equal(PetState.YourTurn,
            PetStateMachine.Aggregate(new[] { busySession, waitingSession }));
    }

    [Fact]
    public void Aggregate_StillDefersToASessionThatIsActuallyWorking()
    {
        // 반대 방향도 지킨다. 설계서 §8 의 원래 의도("일하는 세션이 있으면
        // 그쪽을 보여준다")는 그대로 살아 있다 — 작업이 더 최근이면 이긴다.
        var waitingSession = new PetStateMachine();
        waitingSession.Apply(new TranscriptEvent(TranscriptEventKind.AssistantText)); // 먼저

        var busySession = new PetStateMachine();
        busySession.Apply(Tool("Bash"));                                              // 나중

        Assert.Equal(PetState.Running,
            PetStateMachine.Aggregate(new[] { waitingSession, busySession }));
    }

    [Fact]
    public void Aggregate_IgnoresSessionsThatHaveDoneNothing()
    {
        // 이것이 "창 두 개" 버그의 나머지 절반이었다. 열어만 두고 아무것도 하지
        // 않은 창은 Current=Idle, Sequence=0 인데, 예전 로직은 그것을 "일하는
        // 세션"으로 세어 다른 창의 신호를 통째로 덮었다.
        var justOpened = new PetStateMachine();          // 이벤트 0개
        var turnEnded = new PetStateMachine();
        turnEnded.Apply(new TranscriptEvent(TranscriptEventKind.AssistantText));

        Assert.Equal(PetState.YourTurn,
            PetStateMachine.Aggregate(new[] { justOpened, turnEnded }));
    }

    [Fact]
    public void Aggregate_IgnoresSessionsThatHaveDoneNothing_EvenWhenAnotherIsBlocked()
    {
        // 가장 나빴던 경우: 승인을 기다리며 멈춰 있는데 빈 창 하나가 그것을 가렸다.
        var justOpened = new PetStateMachine();
        var blocked = new PetStateMachine();
        blocked.ApplyNotification("permission_prompt");

        Assert.Equal(PetState.Blocked,
            PetStateMachine.Aggregate(new[] { justOpened, blocked }));
    }

    [Fact]
    public void Aggregate_DoesNotLetAFrozenSessionMaskALiveWaitingSession()
    {
        // 크래시로 Reading 상태에 얼어붙은 세션이 남아 있어도, 그보다 나중에
        // 일어난 신호가 이겨야 한다.
        var frozen = new PetStateMachine();
        frozen.Apply(Tool("Read"));                                                  // 먼저

        var live = new PetStateMachine();
        live.Apply(new TranscriptEvent(TranscriptEventKind.AssistantText));          // 나중

        Assert.Equal(PetState.YourTurn,
            PetStateMachine.Aggregate(new[] { frozen, live }));
    }

    [Fact]
    public void Aggregate_ReturnsIdle_WhenEverySessionIsJustOpen()
    {
        Assert.Equal(PetState.Idle,
            PetStateMachine.Aggregate(new[] { new PetStateMachine(), new PetStateMachine() }));
    }

    [Fact]
    public void Escalation_IsOneWay_LowerLevelDoesNotWeakenTheSignal()
    {
        // 승인 대기 중에 턴 종료 이벤트가 하나 더 들어와도 신호가 약해지면 안 된다.
        var machine = new PetStateMachine();
        machine.ApplyNotification("permission_prompt");
        machine.Apply(new TranscriptEvent(TranscriptEventKind.AssistantText));

        Assert.Equal(NeedsYouLevel.Blocked, machine.NeedsYou);
        Assert.Equal(PetState.Blocked, machine.Current);
    }

    [Fact]
    public void Aggregate_UsesSequenceNotCollectionOrder()
    {
        // Tests that Aggregate picks the session with the highest Sequence,
        // not the last element in the collection.
        // This catches a regression where OrderByDescending(m => m.Sequence)
        // is replaced with LastOrDefault() over the unordered filtered sequence.
        var olderSession = new PetStateMachine();
        olderSession.Apply(Tool("Read"));  // Applied first → lower Sequence

        var newerSession = new PetStateMachine();
        newerSession.Apply(Tool("Bash"));  // Applied second → higher Sequence

        // Pass them in reverse collection order: newerSession first, olderSession second.
        // If the implementation uses collection order instead of Sequence,
        // it would pick olderSession (last element) and return Reading instead of Running.
        Assert.Equal(PetState.Running,
            PetStateMachine.Aggregate(new[] { newerSession, olderSession }));
    }
}
