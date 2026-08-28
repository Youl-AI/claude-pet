using PetCore;

public class UsageTrackerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pet-track-" + Guid.NewGuid().ToString("N"));
    private readonly string _data;

    public UsageTrackerTests()
    {
        _data = Path.Combine(_root, "data");
        Directory.CreateDirectory(Path.Combine(_root, "projects", "proj-a"));
        Directory.CreateDirectory(_data);
    }
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private string ProjectsRoot => Path.Combine(_root, "projects");

    private static string Line(string id, long output) =>
        $$$$"""{"message":{"id":"{{{{id}}}}","model":"claude-opus-5","usage":{"output_tokens":{{{{output}}}}}}}""";

    private string WriteTranscript(string project, string name, params string[] lines)
    {
        var dir = Path.Combine(ProjectsRoot, project);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllLines(path, lines);
        return path;
    }

    private UsageTracker NewTracker() =>
        new(ProjectsRoot, new UsageStore(_data), new TranscriptCostScanner());

    [Fact]
    public void SumsEveryTranscriptUnderTheRoot()
    {
        WriteTranscript("proj-a", "s1.jsonl", Line("m1", 1_000_000));   // $25
        WriteTranscript("proj-b", "s2.jsonl", Line("m2", 1_000_000));   // $25

        var snap = NewTracker().Refresh();

        Assert.Equal(50m, snap.TotalCostUsd);
    }

    [Fact]
    public void FindsTranscriptsInNestedSubagentDirectories()
    {
        WriteTranscript(Path.Combine("proj-a", "s1", "subagents"), "agent-x.jsonl", Line("m1", 1_000_000));

        Assert.Equal(25m, NewTracker().Refresh().TotalCostUsd);
    }

    [Fact]
    public void ReportsTheLevelForTheTotal()
    {
        // opus-5 output $25/1M 이므로 1.2M 토큰 = $30. 원값 29.9819 -> 내림 29.
        WriteTranscript("proj-a", "s1.jsonl", Line("m1", 1_200_000));

        var snap = NewTracker().Refresh();

        Assert.Equal(30m, snap.TotalCostUsd);
        Assert.Equal(LevelCurve.LevelFor(30m), snap.Level);
        Assert.Equal(29, snap.Level);
    }

    [Fact]
    public void UnchangedFilesAreNotRescanned()
    {
        var path = WriteTranscript("proj-a", "s1.jsonl", Line("m1", 1_000_000));

        var store = new UsageStore(_data);
        new UsageTracker(ProjectsRoot, store, new TranscriptCostScanner()).Refresh();

        // 스캐너를 세는 대역으로 바꿔 끼고, 두 번째 Refresh 가 파일을 읽지 않는지 본다.
        var counting = new CountingScanner();
        var snap = new UsageTracker(ProjectsRoot, store, counting).Refresh();

        Assert.Equal(0, counting.Calls);
        Assert.Equal(25m, snap.TotalCostUsd);
    }

    [Fact]
    public void AGrownFileIsRescannedAndTheTotalUpdates()
    {
        var path = WriteTranscript("proj-a", "s1.jsonl", Line("m1", 1_000_000));
        var store = new UsageStore(_data);
        new UsageTracker(ProjectsRoot, store, new TranscriptCostScanner()).Refresh();

        File.AppendAllLines(path, new[] { Line("m2", 1_000_000) });

        var snap = new UsageTracker(ProjectsRoot, store, new TranscriptCostScanner()).Refresh();

        Assert.Equal(50m, snap.TotalCostUsd);
    }

    [Fact]
    public void DisappearedFilesDropOutOfTheTotal()
    {
        var path = WriteTranscript("proj-a", "s1.jsonl", Line("m1", 1_000_000));
        WriteTranscript("proj-a", "s2.jsonl", Line("m2", 1_000_000));
        var store = new UsageStore(_data);
        new UsageTracker(ProjectsRoot, store, new TranscriptCostScanner()).Refresh();

        File.Delete(path);

        Assert.Equal(25m, new UsageTracker(ProjectsRoot, store, new TranscriptCostScanner()).Refresh().TotalCostUsd);
    }

    [Fact]
    public void LeveledUpIsFalseOnTheVeryFirstRefresh()
    {
        // 처음 켰을 때 그동안 쌓인 레벨로 이펙트가 터지면 안 된다.
        WriteTranscript("proj-a", "s1.jsonl", Line("m1", 100_000_000));

        Assert.False(NewTracker().Refresh().LeveledUp);
    }

    [Fact]
    public void LeveledUpIsTrueOnlyWhenTheLevelActuallyRises()
    {
        var path = WriteTranscript("proj-a", "s1.jsonl", Line("m1", 1_200_000));   // $30 -> L30
        var store = new UsageStore(_data);
        new UsageTracker(ProjectsRoot, store, new TranscriptCostScanner()).Refresh();

        // 같은 내용으로 다시 -> 레벨 그대로
        Assert.False(new UsageTracker(ProjectsRoot, store, new TranscriptCostScanner()).Refresh().LeveledUp);

        // 비용을 늘려 레벨이 오르게 한다
        File.AppendAllLines(path, new[] { Line("m2", 4_000_000) });
        Assert.True(new UsageTracker(ProjectsRoot, store, new TranscriptCostScanner()).Refresh().LeveledUp);
    }

    [Fact]
    public void MissingProjectsRootReturnsLevelOneAndDoesNotThrow()
    {
        var tracker = new UsageTracker(Path.Combine(_root, "nope"), new UsageStore(_data), new TranscriptCostScanner());

        var snap = tracker.Refresh();

        Assert.Equal(0m, snap.TotalCostUsd);
        Assert.Equal(1, snap.Level);
        Assert.False(snap.LeveledUp);
    }

    private sealed class CountingScanner : TranscriptCostScanner
    {
        public int Calls;
        public override decimal ScanFile(string path) { Calls++; return 0m; }
    }
}
