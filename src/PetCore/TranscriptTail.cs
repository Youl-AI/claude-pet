using System.Text;

namespace PetCore;

/// <summary>
/// 트랜스크립트 JSONL을 증분 읽기한다.
/// 파일에 어떤 잠금도 걸지 않는다 — Claude Code의 쓰기를 절대 방해해서는 안 된다.
/// </summary>
public sealed class TranscriptTail
{
    /// <summary>
    /// SkipToEnd()가 파일 끝에서부터 거꾸로 훑는 최대 바이트 수.
    /// 이 안에서 마지막 개행을 못 찾으면, 그 이상 뒤로 훑지 않고 파일 끝으로
    /// 건너뛴다 — 부착 시 트랜스크립트 전체를 읽는 것을 막기 위한 명시적 상한이다.
    /// </summary>
    private const int SkipToEndWindowBytes = 64 * 1024;

    private readonly string _path;

    public TranscriptTail(string path) => _path = path;

    public long Position { get; private set; }

    public IReadOnlyList<TranscriptEvent> ReadNew()
    {
        if (!File.Exists(_path))
            return Array.Empty<TranscriptEvent>();

        try
        {
            using var stream = OpenRead();

            if (!TryReadToLastNewline(stream, out var buffer, out var completeLength))
                return Array.Empty<TranscriptEvent>();

            var text = Encoding.UTF8.GetString(buffer, 0, completeLength);

            // Position이 0으로 리셋된 뒤(예: 컴팩션으로 인한 truncation) 그 지점의 바이트가
            // UTF-8 BOM으로 시작하면 GetString이 첫 줄 맨 앞에 U+FEFF를 남긴다. 그대로
            // 두면 JSON 파서가 첫 줄을 실패시켜 조용히 버린다 — 크래시가 아니라 데이터
            // 유실이라 더 위험하다.
            if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];

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

    /// <summary>
    /// 읽은 이벤트를 하나도 돌려주지 않고, 읽기 위치만 파일의 현재 끝 근처로 전진시킨다.
    /// 세션에 처음 부착할 때, 이미 쓰인 트랜스크립트 전체를 재생하지 않고 그 뒤의 새 활동만
    /// 관찰하려는 용도다 — 펫은 라이브 인디케이터지 히스토리 뷰어가 아니고, 그 파일은
    /// 수 메가바이트에 달할 수 있다.
    ///
    /// ReadNew()가 쓰는 TryReadToLastNewline()은 Position부터 "앞으로" 읽으므로 여기서
    /// 재사용하면 안 된다 — 첫 부착 시 Position은 0이라, 파일 전체를 메모리에 올리게 된다.
    /// 대신 파일 끝에서부터 최대 SkipToEndWindowBytes만큼만 "거꾸로" 훑어 그 안의 마지막
    /// 개행을 찾는다. 그 개행 바로 뒤로 Position을 놓아, 다음 ReadNew()가 깨끗한 줄 경계에서
    /// 시작하고 아직 쓰는 중인 마지막 줄을 건너뛰어 유실하지 않게 한다.
    /// </summary>
    public void SkipToEnd()
    {
        if (!File.Exists(_path))
            return;

        try
        {
            using var stream = OpenRead();
            var length = stream.Length;

            var windowSize = (int)Math.Min(SkipToEndWindowBytes, length);
            var windowStart = length - windowSize;
            stream.Seek(windowStart, SeekOrigin.Begin);

            var buffer = new byte[windowSize];
            var totalRead = 0;
            while (totalRead < windowSize)
            {
                var n = stream.Read(buffer, totalRead, windowSize - totalRead);
                if (n == 0) break;   // 예상보다 적게 읽혔다 — 읽은 만큼만 훑는다.
                totalRead += n;
            }

            var lastNewline = totalRead > 0
                ? Array.LastIndexOf(buffer, (byte)'\n', totalRead - 1)
                : -1;

            if (lastNewline >= 0)
            {
                Position = windowStart + lastNewline + 1;
            }
            else
            {
                // 윈도우 안에 개행이 하나도 없다 — 더 뒤로 훑지 않고 파일 끝으로
                // 건너뛴다. 부착은 어차피 이력을 의도적으로 버리는 동작이므로,
                // 이 순간 기록 중이던 줄 한 줄(최대)을 잃는 것이 무제한으로
                // 뒤로 훑는 것보다 낫다.
                Position = length;
            }
        }
        catch (IOException)
        {
            // 절대 던지지 않는다. Position은 그대로 둔다.
        }
        catch (UnauthorizedAccessException)
        {
            // 절대 던지지 않는다. Position은 그대로 둔다.
        }
    }

    private FileStream OpenRead() => new(
        _path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete);

    /// <summary>
    /// 현재 Position부터 스트림 끝까지 읽어, 마지막 완성된 줄(마지막 개행) 뒤까지의
    /// 바이트 수를 계산한다. 마지막 개행 뒤에 남은, 아직 완성되지 않은 바이트는
    /// 절대 건드리지 않는다 — 그래야 두 번의 poll에 걸쳐 쪼개져 쓰인 줄이 다음 poll에서
    /// 완성된 뒤 다시 읽혀 정확히 한 번만 처리된다. 개행(\n)은 UTF-8에서 항상 단독
    /// 1바이트이고 멀티바이트 시퀀스 내부에는 절대 나타나지 않으므로, 개행 경계에서
    /// 자르면 멀티바이트 문자를 반으로 자르는 일도 없다.
    /// </summary>
    private bool TryReadToLastNewline(FileStream stream, out byte[] buffer, out int completeLength)
    {
        buffer = Array.Empty<byte>();
        completeLength = 0;

        if (stream.Length < Position)
            Position = 0;   // 파일이 잘렸다 (컴팩션 등) — 처음부터 다시.

        stream.Seek(Position, SeekOrigin.Begin);

        var toRead = (int)(stream.Length - Position);
        if (toRead <= 0)
            return false;

        buffer = new byte[toRead];
        var totalRead = 0;
        while (totalRead < toRead)
        {
            var n = stream.Read(buffer, totalRead, toRead - totalRead);
            if (n == 0) break;   // 예상보다 적게 읽혔다 — 읽은 만큼만 처리한다.
            totalRead += n;
        }

        if (totalRead == 0)
            return false;

        var lastNewline = Array.LastIndexOf(buffer, (byte)'\n', totalRead - 1);
        if (lastNewline < 0)
            return false;   // 완성된 줄이 아직 없다.

        completeLength = lastNewline + 1;
        return true;
    }
}
