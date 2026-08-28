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
}
