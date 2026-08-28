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

        var working = machines
            .Where(m => m.Current != PetState.NeedsYou)
            .OrderByDescending(m => m.Sequence)
            .FirstOrDefault();

        return working?.Current ?? PetState.NeedsYou;
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
                if (e.IsError) Current = PetState.Error;
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
        Current = PetState.NeedsYou;
    }

    private static PetState Classify(string? toolName)
    {
        if (toolName is null) return PetState.Running;
        if (ReadingTools.Contains(toolName)) return PetState.Reading;
        if (WritingTools.Contains(toolName)) return PetState.Writing;
        return PetState.Running;
    }
}
