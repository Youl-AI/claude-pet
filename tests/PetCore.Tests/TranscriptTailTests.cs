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

    [Fact]
    public void SkipToEnd_LargeTranscript_LandsJustPastFinalNewline_WithoutReadingWholeFile()
    {
        // 회귀 테스트: 옛 구현은 TryReadToLastNewline을 공유해서 Position(=0)부터
        // 파일 끝까지 "앞으로" 전체를 읽었다 — 이 파일 하나가 통째로 메모리에 올라간다.
        // 정직하게 짚자면, 이 테스트는 *결과 위치*만 검증하므로 옛 구현도 (전체 파일을
        // 앞에서부터 훑어) 같은 마지막 개행 위치를 우연히 찾아내 이 assert 자체는 통과할
        // 수 있다 — 그 무제한 읽기라는 바운드 위반은 이 테스트가 직접 잡지 못하고,
        // SkipToEndWindowBytes라는 명시적 상수와 그 상수를 실제로 사용하는 구현으로
        // 강제된다. 그래도 "마지막 개행 뒤로 정확히 이동"이라는 정답 자체는 큰 파일에서도
        // 성립해야 하므로 여기서 검증한다.
        var line = ToolUseLine + "\n";
        var sb = new StringBuilder();
        while (sb.Length < 5 * 1024 * 1024) // 5MB 이상 — "몇 메가바이트" 규모
            sb.Append(line);
        File.WriteAllText(_path, sb.ToString());

        var tail = new TranscriptTail(_path);
        tail.SkipToEnd();

        var expectedPosition = new FileInfo(_path).Length;
        Assert.Equal(expectedPosition, tail.Position);
        Assert.Empty(tail.ReadNew());

        File.AppendAllText(_path, line);
        var events = tail.ReadNew();

        Assert.Single(events);
        Assert.Equal(TranscriptEventKind.ToolUse, events[0].Kind);
    }

    [Fact]
    public void SkipToEnd_NoNewlineWithinWindow_SkipsToFileEnd_AndLosesOnlyThePartialTrailingLine()
    {
        // 스캔 창(윈도우) 안에 개행이 전혀 없는 경우: 파일 끝으로 건너뛰고, 그 순간
        // 기록되던 중이던 미완성 트레일링 줄은 유실을 감수한다. attach는 의도적으로
        // 기존 이력을 버리는 동작이므로, 무제한으로 뒤로 훑어 찾느니 최대 한 줄
        // 유실이 더 낫다는 트레이드오프다.
        File.WriteAllText(_path, ToolUseLine + "\n"); // 완성된 줄 하나로 시작한다.
        // 개행이 전혀 없는 70KB짜리 미완성 트레일링 줄 — 64KB 윈도우보다 크다.
        var partial = new string('x', 70 * 1024);
        File.AppendAllText(_path, partial);

        var tail = new TranscriptTail(_path);
        tail.SkipToEnd();

        var expectedPosition = new FileInfo(_path).Length;
        Assert.Equal(expectedPosition, tail.Position);
        Assert.Empty(tail.ReadNew());

        File.AppendAllText(_path, "\n" + ToolUseLine + "\n");
        var events = tail.ReadNew();

        Assert.Single(events);
        Assert.Equal(TranscriptEventKind.ToolUse, events[0].Kind);
    }
}
