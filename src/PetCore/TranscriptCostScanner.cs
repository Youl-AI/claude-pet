using System.Text;

namespace PetCore;

/// <summary>
/// 트랜스크립트 파일 하나의 누적 비용을 계산한다.
///
/// 중복 제거용 id 집합은 이 메서드 안에서만 살아 있다가 반환과 함께 버려진다. 전체를 기억하면
/// 34,254개 x 약 100 bytes = 3.4 MB 이고 1년이면 40 MB 가 된다 — 펫 전체가 75 MB 인데 그
/// 절반이 id 목록이 되므로 그렇게 하지 않는다 (스펙 §3.3).
/// </summary>
public sealed class TranscriptCostScanner
{
    /// <summary>
    /// 절대 던지지 않는다. 읽을 수 없으면 0을 돌려준다.
    ///
    /// catch-all 은 의도적이다. 이 파일은 Claude Code 가 지금 쓰고 있을 수 있고, 스캔 도중
    /// 지워질 수도 있으며, 권한이 바뀔 수도 있다. 예외 종류를 하나씩 열거하는 방식은 이
    /// 저장소에서 이미 세 번 실패했다.
    /// </summary>
    public decimal ScanFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return 0m;

            // FileShare.ReadWrite | Delete: 쓰는 쪽을 절대 막지 않는다. TranscriptTail 과
            // 같은 계약이다 (설계서 §6.2).
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var total = 0m;

            while (reader.ReadLine() is { } line)
            {
                // usage 없는 줄이 대부분이다. JSON 파싱 전에 값싸게 걸러낸다.
                if (line.Length == 0 || !line.Contains("\"usage\"", StringComparison.Ordinal))
                    continue;

                if (!UsageLineParser.TryParse(line, out var record))
                    continue;

                if (!seen.Add(record.MessageId))
                    continue;

                total += UsagePricing.CostUsd(record.Model, record.Tokens);
            }

            return total;
        }
        catch (Exception)
        {
            return 0m;
        }
    }
}
