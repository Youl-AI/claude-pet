using System.Text;

namespace PetCore;

/// <summary>
/// 트랜스크립트 파일 하나의 누적 비용을 계산한다.
///
/// 중복 제거용 id 집합은 이 메서드 안에서만 살아 있다가 반환과 함께 버려진다. 전체를 기억하면
/// 34,254개 x 약 100 bytes = 3.4 MB 이고 1년이면 40 MB 가 된다 — 펫 전체가 75 MB 인데 그
/// 절반이 id 목록이 되므로 그렇게 하지 않는다 (스펙 §3.3).
///
/// 왜 StreamReader.ReadLine 이 아니라 바이트 버퍼인가 — ReadLine 은 모든 줄을 문자열로
/// 만든다. 트랜스크립트의 대다수 줄에는 usage 가 없어서 만들자마자 버려지는데, 그 할당이
/// 콜드 스캔(수백 파일, GB 단위)에서 GC 세그먼트를 부풀리고 수 MB 줄이 든 활성 파일에서는
/// 30초 주기 재스캔마다 반복됐다. 여기서는 재사용 바이트 버퍼에 줄을 모으고, ASCII
/// "usage" 시퀀스가 바이트로 존재할 때만 문자열로 디코드한다 (UTF-8 에서 ASCII 부분
/// 문자열 검색은 바이트 비교로 정확하다 — 멀티바이트 문자에는 0x80 미만 바이트가 없다).
/// </summary>
public class TranscriptCostScanner
{
    /// <summary>
    /// 한 줄의 상한. 실측된 실사용 최대 줄은 2.39 MB — 이 상한을 넘는 줄은 병리적
    /// 입력으로 보고 디코드 없이 통째로 건너뛴다. usage 줄이 이 크기일 수는 없다.
    /// </summary>
    private const int MaxLineBytes = 8 * 1024 * 1024;

    private const int ChunkBytes = 64 * 1024;

    private static readonly byte[] UsageNeedle = "\"usage\""u8.ToArray();

    // UsageTracker 가 한 인스턴스로 수백 파일을 연속 스캔한다 — 호출 사이에 버퍼를
    // 재사용해 파일당 재할당을 없앤다. 단일 백그라운드 태스크에서만 호출되므로
    // 동기화는 필요 없다 (PetHost 의 _levelRefreshInFlight 가 중첩 실행을 막는다).
    private byte[] _chunk = new byte[ChunkBytes];
    private byte[] _line = new byte[ChunkBytes];

    /// <summary>
    /// 절대 던지지 않는다. 읽을 수 없으면 0을 돌려준다.
    ///
    /// catch-all 은 의도적이다. 이 파일은 Claude Code 가 지금 쓰고 있을 수 있고, 스캔 도중
    /// 지워질 수도 있으며, 권한이 바뀔 수도 있다. 예외 종류를 하나씩 열거하는 방식은 이
    /// 저장소에서 이미 세 번 실패했다.
    /// </summary>
    public virtual decimal ScanFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return 0m;

            // FileShare.ReadWrite | Delete: 쓰는 쪽을 절대 막지 않는다. TranscriptTail 과
            // 같은 계약이다 (설계서 §6.2).
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var total = 0m;

            var lineLength = 0;
            var skippingOversizedLine = false;

            void FinishLine()
            {
                if (!skippingOversizedLine && lineLength > 0)
                {
                    var span = _line.AsSpan(0, lineLength);
                    if (span[^1] == (byte)'\r') span = span[..^1];   // CRLF

                    // usage 없는 줄이 대부분이다. 디코드 전에 바이트로 걸러낸다.
                    if (span.IndexOf(UsageNeedle) >= 0)
                    {
                        var text = Encoding.UTF8.GetString(span);
                        if (UsageLineParser.TryParse(text, out var record)
                            && seen.Add(record.MessageId))
                        {
                            total += UsagePricing.CostUsd(record.Model, record.Tokens);
                        }
                    }
                }
                lineLength = 0;
                skippingOversizedLine = false;
            }

            while (true)
            {
                var read = stream.Read(_chunk, 0, _chunk.Length);
                if (read == 0) break;

                var offset = 0;
                while (offset < read)
                {
                    var nl = Array.IndexOf(_chunk, (byte)'\n', offset, read - offset);
                    var end = nl >= 0 ? nl : read;
                    var count = end - offset;

                    if (!skippingOversizedLine && count > 0)
                    {
                        if (lineLength + count > MaxLineBytes)
                        {
                            // 상한 초과 — 이 줄의 나머지는 개행까지 전부 버린다.
                            skippingOversizedLine = true;
                        }
                        else
                        {
                            if (lineLength + count > _line.Length)
                            {
                                var grown = Math.Max(_line.Length * 2, lineLength + count);
                                Array.Resize(ref _line, Math.Min(grown, MaxLineBytes));
                            }
                            Array.Copy(_chunk, offset, _line, lineLength, count);
                            lineLength += count;
                        }
                    }

                    if (nl < 0) break;      // 청크 끝 — 줄이 다음 청크로 이어진다
                    FinishLine();
                    offset = nl + 1;
                }
            }

            // 개행 없이 끝나는 마지막 줄 — 쓰는 중인 파일에서 흔하다.
            // StreamReader.ReadLine 이 EOF 에서 그 줄을 돌려주던 동작을 보존한다.
            FinishLine();

            return total;
        }
        catch (Exception)
        {
            return 0m;
        }
    }
}
