namespace PetCore;

public enum TranscriptEventKind
{
    ToolUse,
    ToolResult,
    AssistantText,
    Thinking,
    Other
}

public sealed record TranscriptEvent(
    TranscriptEventKind Kind,
    string? ToolName = null,
    bool IsError = false);
