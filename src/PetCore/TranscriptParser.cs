using System.Text.Json;

namespace PetCore;

public static class TranscriptParser
{
    public static IReadOnlyList<TranscriptEvent> ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return Array.Empty<TranscriptEvent>();

        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) { return Array.Empty<TranscriptEvent>(); }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("message", out var message))
                return Array.Empty<TranscriptEvent>();
            if (!message.TryGetProperty("content", out var content))
                return Array.Empty<TranscriptEvent>();

            if (content.ValueKind == JsonValueKind.String)
                return new[] { new TranscriptEvent(TranscriptEventKind.AssistantText) };

            if (content.ValueKind != JsonValueKind.Array)
                return Array.Empty<TranscriptEvent>();

            var results = new List<TranscriptEvent>();
            foreach (var item in content.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out var typeProp))
                    continue;

                switch (typeProp.GetString())
                {
                    case "tool_use":
                        var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                        results.Add(new TranscriptEvent(TranscriptEventKind.ToolUse, name));
                        break;

                    case "tool_result":
                        var isError = item.TryGetProperty("is_error", out var e)
                                      && e.ValueKind == JsonValueKind.True;
                        results.Add(new TranscriptEvent(
                            TranscriptEventKind.ToolResult, null, isError));
                        break;

                    case "text":
                        results.Add(new TranscriptEvent(TranscriptEventKind.AssistantText));
                        break;

                    case "thinking":
                        results.Add(new TranscriptEvent(TranscriptEventKind.Thinking));
                        break;
                }
            }
            return results;
        }
    }
}
