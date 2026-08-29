using System.Diagnostics;
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

    // WriteTranscript 로 연달아 만든 파일들이 실제 벽시계로는 같은 밀리초에 찍혀 (size,
    // mtime) 이 우연히 같아지는 것을 막는다 — 그러면 서로 다른 파일인데도 F2의 중복
    // 제거 로직이 하나로 묶어 세어 총액이 낮게 나온다. 파일마다 1초씩 밀어 준다.
    // 테스트가 두 파일을 일부러 같은 mtime 으로 만들고 싶으면 WriteTranscript 이후에
    // File.SetLastWriteTimeUtc 로 직접 덮어쓰면 된다.
    private DateTime _nextMtime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private string WriteTranscript(string project, string name, params string[] lines)
    {
        var dir = Path.Combine(ProjectsRoot, project);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllLines(path, lines);
        File.SetLastWriteTimeUtc(path, _nextMtime);
        _nextMtime = _nextMtime.AddSeconds(1);
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

    [WindowsOnlyFact]
    public void AFailedEnumerationLeavesThePreviousStateIntactAndDoesNotSpuriouslyLevelUp()
    {
        // SessionRegistryTests의 dangling NTFS junction 기법을 그대로 쓰되, 정션을 트리 중간이
        // 아니라 projectsRoot 자리에 통째로 놓는다. 실측 결과, .NET의 재귀 열거
        // (SearchOption.AllDirectories)는 재귀 도중 만난 하위 디렉터리가 매달린 정션이면
        // 예외 없이 조용히 건너뛴다 — 이 저장소의 관리자 권한 셸에서는 ACL deny 로 하위
        // 폴더 접근을 막아도 백업 시맨틱스 때문에 우회되어 막히지 않는 것도 확인했다.
        // 반면 열거의 *루트 자체*가 매달린 정션이면 Directory.Exists 는 true 를 주면서도
        // .ToList() 가 DirectoryNotFoundException 을 던진다 — 권한과 무관하게 결정적이다.
        // Windows 전용이라는 사실은 [WindowsOnlyFact] 로 명시한다 — 예전처럼 조용히
        // return 하면 다른 OS에서 이 테스트가 아무것도 안 하고도 초록으로 보고된다.

        // 사이클 N: 정상 스캔. 총액 $30, 레벨 29가 저장된다.
        WriteTranscript("proj-a", "s1.jsonl", Line("m1", 1_200_000));
        var store = new UsageStore(_data);
        var before = new UsageTracker(ProjectsRoot, store, new TranscriptCostScanner()).Refresh();
        Assert.Equal(30m, before.TotalCostUsd);
        Assert.Equal(29, before.Level);

        // 사이클 N+1: projectsRoot 자리를 통째로 매달린 정션으로 바꿔치기한다.
        Directory.Delete(ProjectsRoot, recursive: true);
        var target = Path.Combine(_root, $"target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(target);

        var mklink = Process.Start(new ProcessStartInfo(
            "cmd.exe", $"/c mklink /J \"{ProjectsRoot}\" \"{target}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        mklink.WaitForExit();
        Assert.Equal(0, mklink.ExitCode);

        Directory.Delete(target, recursive: true); // 정션은 매달린 채로 남는다
        Assert.True(Directory.Exists(ProjectsRoot), "전제조건: 매달린 정션도 존재하는 것으로 보고되어야 한다");

        // 이 지점 이후 어디서 실패하더라도 정리는 Dispose() 가 맡는다 (SessionRegistryTests와
        // 같은 근거: Directory.Delete(_root, true) 는 정션을 따라가지 않고 그 자체를
        // 안전하게 지운다).
        var failed = new UsageTracker(ProjectsRoot, store, new TranscriptCostScanner()).Refresh();

        // 실패한 사이클은 아무것도 저장하지 않는다 — 직전 스냅샷을 그대로 돌려준다.
        Assert.Equal(30m, failed.TotalCostUsd);
        Assert.Equal(29, failed.Level);
        Assert.False(failed.LeveledUp);

        var persisted = store.Load();
        Assert.Equal(30m, persisted.TotalCostUsd);
        Assert.Equal(29, persisted.Level);

        // 사이클 N+2: 정션을 치우고 같은 내용을 복원하면 다시 정상 스캔된다. previousLevel 이
        // 여전히 29였어야 하므로(사이클 N+1이 1로 덮어쓰지 않았어야 하므로) 가짜 레벨업이
        // 나오면 안 된다.
        Directory.Delete(ProjectsRoot);
        WriteTranscript("proj-a", "s1.jsonl", Line("m1", 1_200_000));
        var after = new UsageTracker(ProjectsRoot, store, new TranscriptCostScanner()).Refresh();

        Assert.Equal(30m, after.TotalCostUsd);
        Assert.Equal(29, after.Level);
        Assert.False(after.LeveledUp);
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

    [Fact]
    public void ATransientPerFileFailureDoesNotLowerTheTotalOrCauseALevelUp()
    {
        // 사이클 N: 정상 스캔. 총액 $30, 레벨 29가 저장된다.
        var path = WriteTranscript("proj-a", "s1.jsonl", Line("m1", 1_200_000));
        var store = new UsageStore(_data);
        var before = new UsageTracker(ProjectsRoot, store, new TranscriptCostScanner()).Refresh();
        Assert.Equal(30m, before.TotalCostUsd);
        Assert.Equal(29, before.Level);

        // 파일 내용을 바꿔 (size/mtime 변경) 캐시 히트를 피하고 재스캔을 유도한다 —
        // 그래야 아래 ThrowingScanner.ScanFile 이 실제로 호출된다.
        File.AppendAllLines(path, new[] { Line("m2", 1_000_000) });

        // 이번 사이클엔 이 파일의 스캔이 실패한다 (일시적 공유 위반/권한 변경 등을 흉내).
        // UsageTracker.Refresh() 안에서 FileInfo stat 과 ScanFile 호출은 같은 per-file
        // try 블록 안에 있으므로, ScanFile 이 던지는 것만으로도 stat 실패와 동일한
        // catch 경로(그리고 동일한 fresh 누락)를 그대로 재현한다.
        var failed = new UsageTracker(ProjectsRoot, store, new ThrowingScanner()).Refresh();

        // 파일이 여전히 존재하므로(File.Exists) 직전 비용을 그대로 들고 가야 한다 —
        // 총액도 레벨도 떨어지면 안 되고, 당연히 레벨업도 아니다.
        Assert.Equal(30m, failed.TotalCostUsd);
        Assert.Equal(29, failed.Level);
        Assert.False(failed.LeveledUp);

        var persisted = store.Load();
        Assert.Equal(30m, persisted.TotalCostUsd);
        Assert.Equal(29, persisted.Level);
    }

    [Fact]
    public void TwoFilesWithTheSameSizeAndMtimeAreCountedOnce()
    {
        // Claude Code 가 worktree 세션의 트랜스크립트를 두 번째 프로젝트 디렉터리로
        // 그대로 복사해 두는 경우 — 바이트 단위로 동일한 파일이 두 경로에 존재한다.
        var pathA = WriteTranscript("proj-a", "s1.jsonl", Line("m1", 1_200_000));   // $30
        var pathB = WriteTranscript("proj-b", "s1-copy.jsonl", Line("m1", 1_200_000));

        var mtime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(pathA, mtime);
        File.SetLastWriteTimeUtc(pathB, mtime);
        Assert.Equal(new FileInfo(pathA).Length, new FileInfo(pathB).Length);

        var snap = NewTracker().Refresh();

        Assert.Equal(30m, snap.TotalCostUsd);
    }

    [Fact]
    public void TwoFilesWithTheSameSizeButDifferentMtimesAreBothCounted()
    {
        var pathA = WriteTranscript("proj-a", "s1.jsonl", Line("m1", 1_200_000));   // $30
        var pathB = WriteTranscript("proj-b", "s2.jsonl", Line("m2", 1_200_000));   // $30
        Assert.Equal(new FileInfo(pathA).Length, new FileInfo(pathB).Length);

        File.SetLastWriteTimeUtc(pathA, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(pathB, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));

        var snap = NewTracker().Refresh();

        Assert.Equal(60m, snap.TotalCostUsd);
    }

    private sealed class ThrowingScanner : TranscriptCostScanner
    {
        public override decimal ScanFile(string path) => throw new IOException("simulated transient failure");
    }

    private sealed class CountingScanner : TranscriptCostScanner
    {
        public int Calls;
        public override decimal ScanFile(string path) { Calls++; return 0m; }
    }
}

/// <summary>
/// Windows 전용 테스트를 xUnit 의 Skip 메커니즘으로 명시적으로 건너뛴다. 예전처럼 테스트
/// 본문 맨 위에서 "if (!OperatingSystem.IsWindows()) return;" 으로 조용히 빠지면, 아무것도
/// 검증하지 않았는데도 결과가 "통과"로 보고되어 아무도 눈치채지 못한다. 이 attribute 를
/// 쓰면 다른 OS에서 "건너뜀"으로 명확히 표시된다.
/// </summary>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows 전용: NTFS 정션(mklink /J) 조작에 의존한다.";
    }
}
