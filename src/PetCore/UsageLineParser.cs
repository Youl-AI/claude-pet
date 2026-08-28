using System.Text.Json;

namespace PetCore;

/// <summary>API 호출 한 건. MessageId 로 중복을 제거한다.</summary>
public readonly record struct UsageRecord(string MessageId, string? Model, TokenCounts Tokens);

/// <summary>
/// 트랜스크립트 JSONL 한 줄에서 usage 를 뽑는다.
///
/// TranscriptParser 와 같은 규율을 따른다: 모든 탐색 지점에서 ValueKind 를 먼저 확인한다.
/// JsonElement.TryGetProperty 는 대상이 객체가 아니면 던진다.
/// </summary>
public static class UsageLineParser
{
    public static bool TryParse(string line, out UsageRecord record)
    {
        record = default;

        if (string.IsNullOrWhiteSpace(line))
            return false;

        // 이 메서드의 계약은 "절대 던지지 않는다"이다. 손상된 줄은 이 파일 어디에나 있을 수
        // 있고(부분 기록, 인코딩 깨짐), 그 한 줄 때문에 스캔 전체가 중단되어서는 안 된다.
        try
        {
            using var doc = JsonDocument.Parse(line);

            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("message", out var message)) return false;
            if (message.ValueKind != JsonValueKind.Object) return false;
            if (!message.TryGetProperty("usage", out var usage)) return false;
            if (usage.ValueKind != JsonValueKind.Object) return false;

            // id 가 없으면 중복 제거를 할 수 없으므로 세지 않는다. 두 번 세는 것보다 낫다.
            if (!message.TryGetProperty("id", out var idProp)) return false;
            if (idProp.ValueKind != JsonValueKind.String) return false;
            var id = idProp.GetString();
            if (string.IsNullOrEmpty(id)) return false;

            var model = message.TryGetProperty("model", out var m)
                        && m.ValueKind == JsonValueKind.String
                ? m.GetString()
                : null;

            record = new UsageRecord(id, model, new TokenCounts(
                Long(usage, "input_tokens"),
                Long(usage, "cache_creation_input_tokens"),
                Long(usage, "cache_read_input_tokens"),
                Long(usage, "output_tokens")));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static long Long(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.Number
        && v.TryGetInt64(out var n)
            ? n
            : 0;
}
