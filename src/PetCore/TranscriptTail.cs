using System.Text;

namespace PetCore;

/// <summary>
/// 트랜스크립트 JSONL을 증분 읽기한다.
/// 파일에 어떤 잠금도 걸지 않는다 — Claude Code의 쓰기를 절대 방해해서는 안 된다.
/// </summary>
public sealed class TranscriptTail
{
    private readonly string _path;

    public TranscriptTail(string path) => _path = path;

    public long Position { get; private set; }

    public IReadOnlyList<TranscriptEvent> ReadNew()
    {
        if (!File.Exists(_path))
            return Array.Empty<TranscriptEvent>();

        try
        {
            using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            if (stream.Length < Position)
                Position = 0;   // 파일이 잘렸다 (컴팩션 등) — 처음부터 다시.

            stream.Seek(Position, SeekOrigin.Begin);

            var toRead = (int)(stream.Length - Position);
            if (toRead <= 0)
                return Array.Empty<TranscriptEvent>();

            var buffer = new byte[toRead];
            var totalRead = 0;
            while (totalRead < toRead)
            {
                var n = stream.Read(buffer, totalRead, toRead - totalRead);
                if (n == 0) break;   // 예상보다 적게 읽혔다 — 읽은 만큼만 처리한다.
                totalRead += n;
            }

            if (totalRead == 0)
                return Array.Empty<TranscriptEvent>();

            // 마지막 개행 뒤에 남은, 아직 완성되지 않은 바이트는 절대 건드리지 않는다.
            // Position은 여기서 실제로 소비한 바이트 수만큼만 전진시키며,
            // (버그였던) 재조회한 stream.Length로는 절대 전진시키지 않는다 — 그래야
            // 두 번의 poll에 걸쳐 쪼개져 쓰인 줄이 다음 poll에서 완성된 뒤 다시 읽혀
            // 정확히 한 번만 파싱된다. 개행(\n)은 UTF-8에서 항상 단독 1바이트이고
            // 멀티바이트 시퀀스 내부에는 절대 나타나지 않으므로, 개행 경계에서 자르면
            // 멀티바이트 문자를 반으로 자르는 일도 없다.
            var lastNewline = Array.LastIndexOf(buffer, (byte)'\n', totalRead - 1);
            if (lastNewline < 0)
                return Array.Empty<TranscriptEvent>();   // 완성된 줄이 아직 없다.

            var completeLength = lastNewline + 1;
            var text = Encoding.UTF8.GetString(buffer, 0, completeLength);

            var events = new List<TranscriptEvent>();
            foreach (var rawLine in text.Split('\n'))
            {
                // \r\n 줄바꿈이면 트레일링 \r을 파서에 넘기지 않는다.
                var line = rawLine.Length > 0 && rawLine[^1] == '\r' ? rawLine[..^1] : rawLine;
                if (line.Length == 0)
                    continue;
                events.AddRange(TranscriptParser.ParseLine(line));
            }

            Position += completeLength;
            return events;
        }
        catch (IOException)
        {
            // 일시적 경합. 다음 주기에 다시 읽는다. 절대 던지지 않는다.
            return Array.Empty<TranscriptEvent>();
        }
        catch (UnauthorizedAccessException)
        {
            // NTFS는 삭제 대기(pending-delete) 상태의 파일에서 IOException이 아니라
            // UnauthorizedAccessException을 던질 수 있다. 이 클래스는 FileShare.Delete로
            // 여는 것이 계약이므로(쓰기 프로세스를 방해하면 안 되니까), 동시 삭제와
            // File.Exists 검사 사이의 경합으로 이 상태에 실제로 도달할 수 있다.
            // "절대 던지지 않는다"는 계약을 지키려면 IOException 하나만으로는 부족하다.
            return Array.Empty<TranscriptEvent>();
        }
    }
}
