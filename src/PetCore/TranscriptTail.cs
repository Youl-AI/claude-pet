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

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var events = new List<TranscriptEvent>();
            string? line;
            while ((line = reader.ReadLine()) is not null)
                events.AddRange(TranscriptParser.ParseLine(line));

            Position = stream.Length;
            return events;
        }
        catch (IOException)
        {
            // 일시적 경합. 다음 주기에 다시 읽는다. 절대 던지지 않는다.
            return Array.Empty<TranscriptEvent>();
        }
    }
}
