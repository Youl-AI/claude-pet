using System.Globalization;
using System.Text.RegularExpressions;

namespace PetCore;

/// <summary>
/// 한도 도달 문구에서 리셋 예정 시각을 뽑는다.
///
/// 실측 문구: "You've hit your session limit · resets 6:10pm (Asia/Seoul)".
/// 괄호 안 타임존은 사용자의 로컬 설정을 반영해 찍히므로 따로 해석하지 않고
/// now(호출자의 로컬 오프셋)를 그대로 쓴다. 해석한 시각이 과거면 다음 날로
/// 넘긴다 — 밤 11시에 "resets 2:20am"을 보는 경우다. (스펙 §1.3)
/// </summary>
public static partial class RateLimitReset
{
    [GeneratedRegex(@"resets\s+(\d{1,2}):(\d{2})\s*(am|pm)", RegexOptions.IgnoreCase)]
    private static partial Regex ResetsPattern();

    /// <summary>파싱 실패는 null이다. 절대 던지지 않는다.</summary>
    public static long? Resolve(string? text, DateTimeOffset now)
    {
        try
        {
            if (string.IsNullOrEmpty(text)) return null;

            var m = ResetsPattern().Match(text);
            if (!m.Success) return null;

            var hour = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var minute = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            if (hour is < 1 or > 12 || minute > 59) return null;

            var pm = m.Groups[3].Value.Equals("pm", StringComparison.OrdinalIgnoreCase);
            var hour24 = (hour % 12) + (pm ? 12 : 0);   // 12am -> 0시, 12pm -> 12시

            var reset = new DateTimeOffset(
                now.Year, now.Month, now.Day, hour24, minute, 0, now.Offset);
            if (reset <= now) reset = reset.AddDays(1);

            return reset.ToUnixTimeMilliseconds();
        }
        catch (Exception)
        {
            return null;   // 문구는 신뢰할 수 없는 입력이다. 실패는 "시각 모름"이다.
        }
    }
}
