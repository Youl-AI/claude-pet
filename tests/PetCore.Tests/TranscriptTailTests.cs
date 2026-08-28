using System.Text;
using PetCore;
using Xunit;

public class TranscriptTailTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tail-{Guid.NewGuid():N}.jsonl");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private const string ToolUseLine =
        """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t","name":"Bash","input":{}}]}}""";

    [Fact]
    public void ReadNew_ReturnsOnlyLinesAppendedSinceLastCall()
    {
        File.WriteAllText(_path, ToolUseLine + "\n");
        var tail = new TranscriptTail(_path);

        Assert.Single(tail.ReadNew());
        Assert.Empty(tail.ReadNew());

        File.AppendAllText(_path, ToolUseLine + "\n");
        Assert.Single(tail.ReadNew());
    }

    [Fact]
    public void ReadNew_DoesNotBlockConcurrentWriter()
    {
        // 방해 0 검증: Claude Code가 이미 쓰기 핸들을 열어 쥐고 있는 상태에서도
        // 펫의 ReadNew()가 실제로 성공해서 내용을 읽어와야 한다 (예외가 없다는 것만으로는
        // 부족하다 — ReadNew()는 IOException을 삼키므로, 공유 모드가 깨져도
        // "예외 없음"은 여전히 참이 된다. 그래서 반환된 이벤트의 내용을 검증한다).
        File.WriteAllText(_path, ToolUseLine + "\n");
        var tail = new TranscriptTail(_path);

        using var writer = new FileStream(
            _path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var bytes = Encoding.UTF8.GetBytes(ToolUseLine + "\n");

        // 쓰기 핸들을 먼저 열어서 쥔 채로 두 번째 줄을 쓴다 — 그 다음에야 펫이 읽는다.
        // 이렇게 해야 펫의 Open이 실제로 살아있는 writer 핸들과 경합한다.
        writer.Write(bytes, 0, bytes.Length);
        writer.Flush();

        var events = tail.ReadNew();

        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal(TranscriptEventKind.ToolUse, e.Kind));
    }

    [Fact]
    public void ReadNew_ReadsIncrementally_WhileWriterHandleStaysOpenAcrossPolls()
    {
        // 역방향도 커버: writer 핸들이 여러 번의 poll에 걸쳐 계속 열려있는 상태에서도
        // 펫이 매번 정확한 증분만 읽어야 한다. 스레드나 sleep 없이 결정적으로 구성.
        File.WriteAllText(_path, string.Empty);
        var tail = new TranscriptTail(_path);
        Assert.Empty(tail.ReadNew());

        using var writer = new FileStream(
            _path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var bytes = Encoding.UTF8.GetBytes(ToolUseLine + "\n");

        writer.Write(bytes, 0, bytes.Length);
        writer.Flush();
        var firstPoll = tail.ReadNew();
        Assert.Single(firstPoll);
        Assert.Equal(TranscriptEventKind.ToolUse, firstPoll[0].Kind);

        writer.Write(bytes, 0, bytes.Length);
        writer.Flush();
        var secondPoll = tail.ReadNew();
        Assert.Single(secondPoll);
        Assert.Equal(TranscriptEventKind.ToolUse, secondPoll[0].Kind);
    }

    [Fact]
    public void ReadNew_DeliversLineExactlyOnce_WhenSplitAcrossTwoPolls()
    {
        // 한 줄이 두 번의 poll에 걸쳐 반으로 쪼개져 쓰이는 상황을 재현한다.
        // 완성되지 않은 줄은 절대 파싱되어서도, 소비되어서도 안 되고,
        // 완성된 뒤 정확히 한 번만 전달돼야 한다 (영구 유실 금지).
        var fullLine = ToolUseLine + "\n";
        var splitPoint = fullLine.Length / 2;
        var firstHalf = fullLine[..splitPoint];
        var secondHalf = fullLine[splitPoint..];

        File.WriteAllText(_path, firstHalf);
        var tail = new TranscriptTail(_path);

        // 줄이 아직 개행으로 끝나지 않았으므로 이 poll에서는 아무것도 나오면 안 된다.
        Assert.Empty(tail.ReadNew());

        File.AppendAllText(_path, secondHalf);
        var events = tail.ReadNew();

        Assert.Single(events);
        Assert.Equal(TranscriptEventKind.ToolUse, events[0].Kind);
    }

    [Fact]
    public void ReadNew_ReturnsEmpty_WhenFileMissing()
    {
        var tail = new TranscriptTail(Path.Combine(Path.GetTempPath(), "does-not-exist.jsonl"));
        Assert.Empty(tail.ReadNew());
    }

    [Fact]
    public void ReadNew_RestartsFromZero_WhenFileTruncated()
    {
        File.WriteAllText(_path, ToolUseLine + "\n" + ToolUseLine + "\n");
        var tail = new TranscriptTail(_path);
        Assert.Equal(2, tail.ReadNew().Count);

        File.WriteAllText(_path, ToolUseLine + "\n");
        Assert.Single(tail.ReadNew());
    }

    [Fact]
    public void SkipToEnd_ThenReadNew_ReturnsNothing_ForExistingContent()
    {
        File.WriteAllText(_path, ToolUseLine + "\n" + ToolUseLine + "\n");
        var tail = new TranscriptTail(_path);

        tail.SkipToEnd();

        Assert.Empty(tail.ReadNew());
    }

    [Fact]
    public void SkipToEnd_ContentAppendedAfterSkip_IsStillDelivered()
    {
        File.WriteAllText(_path, ToolUseLine + "\n");
        var tail = new TranscriptTail(_path);

        tail.SkipToEnd();
        Assert.Empty(tail.ReadNew());

        File.AppendAllText(_path, ToolUseLine + "\n");
        var events = tail.ReadNew();

        Assert.Single(events);
        Assert.Equal(TranscriptEventKind.ToolUse, events[0].Kind);
    }

    [Fact]
    public void SkipToEnd_DoesNotSkipPartiallyWrittenTrailingLine_DeliveredOnceCompleted()
    {
        // 마지막 줄이 아직 개행으로 끝나지 않은 채로 SkipToEnd가 호출되면,
        // 그 미완성 줄은 건너뛰어져서 영영 유실되면 안 된다 — 완성된 뒤
        // 다음 ReadNew()에서 정확히 한 번 전달돼야 한다.
        File.WriteAllText(_path, ToolUseLine + "\n");
        var fullLine = ToolUseLine + "\n";
        File.AppendAllText(_path, fullLine[..(fullLine.Length / 2)]);

        var tail = new TranscriptTail(_path);
        tail.SkipToEnd();

        // 아직 미완성이므로 이 시점엔 아무것도 없다.
        Assert.Empty(tail.ReadNew());

        File.AppendAllText(_path, fullLine[(fullLine.Length / 2)..]);
        var events = tail.ReadNew();

        Assert.Single(events);
        Assert.Equal(TranscriptEventKind.ToolUse, events[0].Kind);
    }

    [Fact]
    public void SkipToEnd_MissingFile_DoesNotThrow_AndLeavesPositionAtZero()
    {
        var tail = new TranscriptTail(Path.Combine(Path.GetTempPath(), "does-not-exist.jsonl"));

        var exception = Record.Exception(() => tail.SkipToEnd());

        Assert.Null(exception);
        Assert.Equal(0, tail.Position);
    }
}
