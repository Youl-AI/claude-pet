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
            if (root.ValueKind != JsonValueKind.Object)
                return Array.Empty<TranscriptEvent>();

            // 한도 도달 줄은 assistant text 형태라 아래 규칙대로면 "턴 종료"로
            // 오분류된다. 최상위 error 필드를 먼저 본다 — 정확히 "rate_limit"만.
            // authentication_failed / server_error 도 같은 자리에 오지만(실측)
            // 그것들은 낮잠이 아니다. (스펙 §1.1)
            if (root.TryGetProperty("error", out var errorProp)
                && errorProp.ValueKind == JsonValueKind.String
                && errorProp.GetString() == "rate_limit")
            {
                return new[]
                {
                    new TranscriptEvent(
                        TranscriptEventKind.RateLimited,
                        ResetAtUnixMs: RateLimitReset.Resolve(
                            FirstTextContent(root), DateTimeOffset.Now)),
                };
            }

            if (!root.TryGetProperty("message", out var message))
                return Array.Empty<TranscriptEvent>();
            if (message.ValueKind != JsonValueKind.Object)
                return Array.Empty<TranscriptEvent>();
            if (!message.TryGetProperty("content", out var content))
                return Array.Empty<TranscriptEvent>();

            // message.role 로 키를 잡는다: 이미 읽어 둔 message 객체에 바로 붙어 있고,
            // 이 content 배열/문자열을 실제로 누가 작성했는지 말해 주는 필드이기 때문이다
            // (루트의 "type"은 봉투 분류일 뿐 — user/assistant 줄에서는 role과 일치하지만,
            // "summary" 등 다른 타입은 애초에 message 필드가 없어 위에서 이미 걸러진다).
            // tool_use/tool_result 는 role과 무관하게 항상 파싱한다 — user 줄에 실리는
            // tool_result 가 그 예다.
            var isAssistant = message.TryGetProperty("role", out var roleProp)
                               && roleProp.ValueKind == JsonValueKind.String
                               && roleProp.GetString() == "assistant";

            if (content.ValueKind == JsonValueKind.String)
                return isAssistant
                    ? new[] { new TranscriptEvent(TranscriptEventKind.AssistantText) }
                    : Array.Empty<TranscriptEvent>();

            if (content.ValueKind != JsonValueKind.Array)
                return Array.Empty<TranscriptEvent>();

            var results = new List<TranscriptEvent>();
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                if (!item.TryGetProperty("type", out var typeProp))
                    continue;
                if (typeProp.ValueKind != JsonValueKind.String)
                    continue;

                switch (typeProp.GetString())
                {
                    case "tool_use":
                        var name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                            ? n.GetString()
                            : null;
                        results.Add(new TranscriptEvent(TranscriptEventKind.ToolUse, name));
                        break;

                    case "tool_result":
                        var isError = item.TryGetProperty("is_error", out var e)
                                      && e.ValueKind == JsonValueKind.True;
                        results.Add(new TranscriptEvent(
                            TranscriptEventKind.ToolResult, null, isError));
                        break;

                    case "text":
                        if (isAssistant)
                            results.Add(new TranscriptEvent(TranscriptEventKind.AssistantText));
                        break;

                    case "thinking":
                        if (isAssistant)
                            results.Add(new TranscriptEvent(TranscriptEventKind.Thinking));
                        break;
                }
            }
            return results;
        }
    }

    /// <summary>message.content 배열에서 첫 text 블록의 문자열. 없으면 null.</summary>
    private static string? FirstTextContent(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.Object) return null;
        if (!message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array) return null;

        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("type", out var t)
                && t.ValueKind == JsonValueKind.String
                && t.GetString() == "text"
                && item.TryGetProperty("text", out var txt)
                && txt.ValueKind == JsonValueKind.String)
                return txt.GetString();
        }
        return null;
    }
}
