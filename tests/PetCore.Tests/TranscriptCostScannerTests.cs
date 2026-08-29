using PetCore;

public class TranscriptCostScannerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pet-scan-" + Guid.NewGuid().ToString("N"));

    public TranscriptCostScannerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string WriteFile(string name, params string[] lines)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllLines(path, lines);
        return path;
    }

    private static string Line(string id, string model, long output) =>
        $$$$"""{"message":{"id":"{{{{id}}}}","model":"{{{{model}}}}","usage":{"input_tokens":0,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":{{{{output}}}}}}}""";

    [Fact]
    public void SumsCostAcrossDistinctMessages()
    {
        // opus-5 output $25/1M. 1M + 1M = $50.
        var path = WriteFile("a.jsonl",
            Line("msg_1", "claude-opus-5", 1_000_000),
            Line("msg_2", "claude-opus-5", 1_000_000));

        Assert.Equal(50m, new TranscriptCostScanner().ScanFile(path));
    }

    [Fact]
    public void CountsARepeatedMessageIdExactlyOnce()
    {
        // 실측: usage 줄의 56.9%가 중복이다. 한 응답이 content 블록별로 여러 줄에 기록되고
        // 모든 줄이 같은 message.id 와 동일한 usage 를 담는다 (스펙 §3.2).
        var path = WriteFile("dup.jsonl",
            Line("msg_1", "claude-opus-5", 1_000_000),
            Line("msg_1", "claude-opus-5", 1_000_000),
            Line("msg_1", "claude-opus-5", 1_000_000));

        Assert.Equal(25m, new TranscriptCostScanner().ScanFile(path));
    }

    [Fact]
    public void CountsARepeatedMessageIdOnceEvenWhenTheUsagePayloadsDiffer()
    {
        // 위 CountsARepeatedMessageIdExactlyOnce 의 고정물은 세 줄이 바이트 단위로 완전히
        // 동일하다 — 그래서 "줄 자체"를 HashSet 에 넣는 잘못된 구현도 이 테스트를 통과한다.
        // 여기서는 같은 message.id 인데 usage 값이 서로 다른 두 줄을 넣어, 중복 판정이
        // 실제로 message.id 로 이뤄지는지(줄 내용이 아니라) 못박는다. 두 번째 줄의 훨씬 큰
        // 비용이 더해지면 안 된다 — 첫 번째 payload만 세어야 한다.
        var path = WriteFile("dup-diff-payload.jsonl",
            Line("msg_1", "claude-opus-5", 1_000_000),   // $25 — 이것만 세어져야 한다
            Line("msg_1", "claude-opus-5", 4_000_000));  // $100 — 같은 id 라 버려져야 한다

        Assert.Equal(25m, new TranscriptCostScanner().ScanFile(path));
    }

    [Fact]
    public void DeduplicatesEvenWhenRepeatsAreFarApart()
    {
        // 실측: 같은 id 가 최대 6,416줄 떨어져 다시 나온다. 인접 줄만 보는 방식으로는 못 잡는다.
        var lines = new List<string> { Line("msg_far", "claude-opus-5", 1_000_000) };
        for (var i = 0; i < 500; i++)
            lines.Add("""{"type":"user","message":{"role":"user","content":"filler"}}""");
        lines.Add(Line("msg_far", "claude-opus-5", 1_000_000));

        var path = WriteFile("far.jsonl", lines.ToArray());

        Assert.Equal(25m, new TranscriptCostScanner().ScanFile(path));
    }

    [Fact]
    public void SkipsMalformedLinesAndKeepsGoing()
    {
        var path = WriteFile("mixed.jsonl",
            "not json",
            Line("msg_1", "claude-opus-5", 1_000_000),
            "{\"message\":",
            // "usage" 라는 글자를 담고 있어 값싼 사전 필터(Contains("\"usage\""))를 통과한
            // 뒤 실제 파서(UsageLineParser.TryParse)의 예외 경로까지 도달하는 손상된 줄.
            // 이게 없으면 이 테스트의 "malformed" 줄들은 전부 필터에서 걸러져 파서 자체는
            // 한 번도 실패 경로를 타지 않는다.
            "{\"message\":{\"usage\":",
            "",
            Line("msg_2", "claude-opus-5", 1_000_000));

        Assert.Equal(50m, new TranscriptCostScanner().ScanFile(path));
    }

    [Fact]
    public void MissingFileReturnsZeroAndDoesNotThrow()
    {
        Assert.Equal(0m, new TranscriptCostScanner().ScanFile(Path.Combine(_dir, "nope.jsonl")));
    }

    [Fact]
    public void EmptyFileReturnsZero()
    {
        Assert.Equal(0m, new TranscriptCostScanner().ScanFile(WriteFile("empty.jsonl")));
    }

    [Fact]
    public void ReadsAFileThatIsOpenForWritingElsewhere()
    {
        // Claude Code 가 지금 쓰고 있는 파일을 읽어야 한다. 잠그면 안 된다.
        var path = WriteFile("live.jsonl", Line("msg_1", "claude-opus-5", 1_000_000));

        using var writer = new FileStream(path, FileMode.Append, FileAccess.Write,
                                          FileShare.ReadWrite | FileShare.Delete);

        Assert.Equal(25m, new TranscriptCostScanner().ScanFile(path));
    }

    // --- 바이트 스캐너 전환 (RAM 절감) 관련 ---

    [Fact]
    public void ALineAboveTheCapIsSkippedAndTheRestStillCounts()
    {
        // 상한(8MB)을 넘는 병리적 줄은 통째로 건너뛴다 — 문자열로 만들지도 않는다.
        // 실측 최대 실사용 줄은 2.39MB 라 정상 데이터는 상한에 걸리지 않는다.
        var hugePayload = new string('x', 9 * 1024 * 1024);
        var hugeLine = $$$$"""{"message":{"id":"msg_huge","model":"claude-opus-5","usage":{"output_tokens":1000000},"content":[{"type":"text","text":"{{{{hugePayload}}}}"}]}}""";
        var path = WriteFile("cap.jsonl",
            Line("msg_1", "claude-opus-5", 1_000_000),
            hugeLine,
            Line("msg_2", "claude-opus-5", 1_000_000));

        Assert.Equal(50m, new TranscriptCostScanner().ScanFile(path));
    }

    [Fact]
    public void ABigButLegalLineStillCounts()
    {
        // 상한 이하의 큰 줄(수백 KB)은 정상 데이터다 — 그대로 계산돼야 한다.
        var payload = new string('y', 300 * 1024);
        var bigLine = $$$$"""{"message":{"id":"msg_big","model":"claude-opus-5","usage":{"input_tokens":0,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":1000000},"content":[{"type":"text","text":"{{{{payload}}}}"}]}}""";
        var path = WriteFile("big.jsonl", bigLine);

        Assert.Equal(25m, new TranscriptCostScanner().ScanFile(path));
    }

    [Fact]
    public void AFinalLineWithoutANewlineStillCounts()
    {
        // 쓰는 중인 파일은 마지막 줄에 개행이 없을 수 있다. StreamReader.ReadLine 과
        // 같은 동작을 보존한다.
        var path = Path.Combine(_dir, "noeol.jsonl");
        File.WriteAllText(path, Line("msg_1", "claude-opus-5", 1_000_000));   // 개행 없음

        Assert.Equal(25m, new TranscriptCostScanner().ScanFile(path));
    }

    [Fact]
    public void HandlesLfOnlyLineEndings()
    {
        // WriteAllLines 는 CRLF 를 쓴다. LF 전용 파일도 같은 결과여야 한다.
        var path = Path.Combine(_dir, "lf.jsonl");
        File.WriteAllText(path, Line("msg_1", "claude-opus-5", 1_000_000) + "\n"
                                + Line("msg_2", "claude-opus-5", 1_000_000) + "\n");

        Assert.Equal(50m, new TranscriptCostScanner().ScanFile(path));
    }

    [Fact]
    public void SameScannerInstanceHandlesManyFilesInARow()
    {
        // UsageTracker 는 한 인스턴스로 수백 파일을 연속 스캔한다 — 내부 버퍼
        // 재사용이 파일 간 상태를 오염시키면 안 된다.
        var scanner = new TranscriptCostScanner();
        var a = WriteFile("seq-a.jsonl", Line("msg_1", "claude-opus-5", 1_000_000));
        var b = WriteFile("seq-b.jsonl", Line("msg_1", "claude-opus-5", 2_000_000));

        Assert.Equal(25m, scanner.ScanFile(a));
        Assert.Equal(50m, scanner.ScanFile(b));   // 같은 id 지만 다른 파일 — 별개로 센다
    }
}
