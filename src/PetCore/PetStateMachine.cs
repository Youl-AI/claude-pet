namespace PetCore;

public enum NeedsYouLevel
{
    None = 0,
    YourTurn = 1,    // 턴 종료 — "당신 차례입니다"
    Blocked = 2,     // permission_prompt — "권한 대기로 막혔습니다"
    Abandoned = 3    // idle_prompt (60초) — "아직 안 오셨네요"
}

public sealed class PetStateMachine
{
    private static readonly HashSet<string> ReadingTools =
        new(StringComparer.OrdinalIgnoreCase)
        { "Read", "Grep", "Glob", "WebFetch", "WebSearch" };

    private static readonly HashSet<string> WritingTools =
        new(StringComparer.OrdinalIgnoreCase)
        { "Edit", "Write", "NotebookEdit" };

    private static long _globalSequence;

    public PetState Current { get; private set; } = PetState.Idle;
    public NeedsYouLevel NeedsYou { get; private set; } = NeedsYouLevel.None;

    /// <summary>최근 활동 순서 비교용. 이벤트를 적용할 때마다 증가한다.</summary>
    public long Sequence { get; private set; }

    /// <summary>
    /// 여러 세션의 상태를 하나로 합친다 (스펙 §8).
    /// 일하는 세션이 하나라도 있으면 그중 가장 최근 것을 연기하고,
    /// 모든 세션이 사람을 기다릴 때만 대기 신호를 낸다.
    /// </summary>
    public static PetState Aggregate(IReadOnlyCollection<PetStateMachine> machines)
    {
        if (machines.Count == 0) return PetState.Idle;

        // 1) 사람이 없으면 진행이 안 되는 세션(권한 대기 / 60초 방치)이 하나라도
        //    있으면 그것이 이긴다. 이 신호는 사람이 응답하기 전까지 저절로
        //    사라지지 않으므로, 다른 세션의 최신 활동에 밀려 묻혀서는 안 된다.
        //    (예전 로직은 반대였다: 일하는 세션이 하나라도 있으면 그쪽을 보여줬고,
        //     그래서 창 하나가 1/2/3 승인을 기다리며 멈춰 있는데 다른 창이 작업
        //     중이면 펫이 아무 신호도 내지 않았다.)
        var stuck = machines
            .Where(m => m.NeedsYou >= NeedsYouLevel.Blocked)
            .OrderByDescending(m => m.Sequence)
            .FirstOrDefault();
        if (stuck is not null) return stuck.Current;

        // 2) 그 외에는 "가장 최근에 실제로 무언가 일어난 세션"을 보여준다.
        //    Sequence == 0 은 이벤트를 한 번도 처리하지 않은 세션 — 열어만 두고
        //    아무것도 안 한 창이다. 예전 로직은 이것을 "일하는 세션"으로 세는
        //    바람에, 두 번째 창을 열어두기만 해도 첫 번째 창의 턴 종료 신호가
        //    통째로 가려졌다. Sequence > 0 조건이 그 경로를 막는다.
        //
        //    대기 상태를 미리 걸러내지 않는 것도 의도적이다. 예전에는 필터가
        //    정렬보다 먼저라 대기 세션이 최신순 경쟁에 아예 참가하지 못했고,
        //    몇 시간 전에 얼어붙은 세션이 방금 대기 상태가 된 세션을 이겼다.
        var newest = machines
            .Where(m => m.Sequence > 0)
            .OrderByDescending(m => m.Sequence)
            .FirstOrDefault();

        return newest?.Current ?? PetState.Idle;
    }

    public void Apply(TranscriptEvent e)
    {
        Sequence = Interlocked.Increment(ref _globalSequence);

        switch (e.Kind)
        {
            case TranscriptEventKind.ToolUse:
                NeedsYou = NeedsYouLevel.None;
                Current = Classify(e.ToolName);
                break;

            case TranscriptEventKind.ToolResult:
                if (e.IsError)
                {
                    Current = PetState.Error;
                }
                else
                {
                    // 성공한 결과는 일이 진행 중이라는 뜻이다 — 승인 대기(Blocked)든
                    // 같은 턴의 다른 도구가 남긴 Error든, 더 이상 사실이 아닌 신호를 지운다.
                    // 다음 도구 호출이나 어시스턴트 텍스트가 오기 전까지는 Thinking과 동일하게
                    // "지금 특정 동작 중은 아니다"가 가장 정직한 상태이므로 Idle로 되돌린다.
                    NeedsYou = NeedsYouLevel.None;
                    Current = PetState.Idle;
                }
                break;

            case TranscriptEventKind.AssistantText:
                // tool_use 없는 assistant 메시지 = 턴 종료. 휴리스틱이 아니라 규칙이다.
                Escalate(NeedsYouLevel.YourTurn);
                break;

            case TranscriptEventKind.Thinking:
                NeedsYou = NeedsYouLevel.None;
                Current = PetState.Idle;
                break;
        }
    }

    public void ApplyNotification(string notificationType)
    {
        Sequence = Interlocked.Increment(ref _globalSequence);

        switch (notificationType)
        {
            case "permission_prompt":
                Escalate(NeedsYouLevel.Blocked);
                break;
            case "idle_prompt":
                Escalate(NeedsYouLevel.Abandoned);
                break;
        }
    }

    private void Escalate(NeedsYouLevel level)
    {
        if (level > NeedsYou) NeedsYou = level;

        // 화면 상태는 "지금 들어온 이벤트"가 아니라 "지금까지 도달한 최고 단계"를
        // 따른다. 그래야 권한 대기(Blocked) 중에 턴 종료(YourTurn) 이벤트가 하나
        // 더 들어와도 신호가 약해지지 않는다 — 승격은 한 방향으로만 간다.
        Current = NeedsYou switch
        {
            NeedsYouLevel.Abandoned => PetState.Abandoned,
            NeedsYouLevel.Blocked   => PetState.Blocked,
            _                       => PetState.YourTurn,
        };
    }

    private static PetState Classify(string? toolName)
    {
        if (toolName is null) return PetState.Running;
        if (ReadingTools.Contains(toolName)) return PetState.Reading;
        if (WritingTools.Contains(toolName)) return PetState.Writing;
        return PetState.Running;
    }
}
