using System.Diagnostics;
using PetCore;
using Xunit;

public class SessionRegistryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sessions-{Guid.NewGuid():N}");

    public SessionRegistryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private void WriteSession(string id, string json) =>
        File.WriteAllText(Path.Combine(_dir, $"{id}.json"), json);

    // TTL 스윕(orphan 정리) 대상이 되지 않도록, TTL과 무관한 테스트는 이 "지금"에
    // 가까운 touchedUnixMs를 쓴다. 7일 TTL을 다투는 테스트만 별도로 고정된
    // now/touched 쌍을 쓴다.
    private static readonly long FreshTouched = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [Fact]
    public void ReadAll_ParsesWellFormedRecords()
    {
        WriteSession("abc", $$"""
        {"sessionId":"abc","transcriptPath":"C:\\t\\abc.jsonl","pid":1234,"pidStartUnixMs":111,"touchedUnixMs":{{FreshTouched}}}
        """);

        var r = Assert.Single(new SessionRegistry(_dir).ReadAll());
        Assert.Equal("abc", r.SessionId);
        Assert.Equal(1234, r.Pid);
        Assert.Equal(111, r.PidStartUnixMs);
    }

    [Fact]
    public void ReadAll_SkipsCorruptFilesInsteadOfThrowing()
    {
        WriteSession("good", $$"""
        {"sessionId":"good","transcriptPath":"p","pid":1,"pidStartUnixMs":0,"touchedUnixMs":{{FreshTouched}}}
        """);
        WriteSession("bad", "{ this is not json");

        var records = new SessionRegistry(_dir).ReadAll();

        Assert.Single(records);
        Assert.Equal("good", records[0].SessionId);
    }

    [Fact]
    public void ReadAll_ReturnsEmpty_WhenDirectoryMissing()
    {
        var missing = Path.Combine(_dir, "nope");
        Assert.Empty(new SessionRegistry(missing).ReadAll());
    }

    [Fact]
    public void ReadAll_DoesNotThrow_WhenDirectoryPathIsActuallyAFile()
    {
        // Directory.Exists returns false for a path that points at a file,
        // so this exercises the "past the existence check never happens" path,
        // but still proves ReadAll's public contract: never throw, regardless
        // of what garbage the directory path resolves to.
        var filePath = Path.Combine(_dir, "not-a-directory.txt");
        File.WriteAllText(filePath, "just a file");

        var records = new SessionRegistry(filePath).ReadAll();

        Assert.Empty(records);
    }

    [Fact]
    public void ReadAll_DoesNotThrow_WhenEnumerationFailsMidIteration()
    {
        // This test relies on Windows NTFS junction reparse points, which are not
        // portable. On non-Windows platforms, the junction trick is unavailable.
        if (!OperatingSystem.IsWindows()) return;

        // Deterministically reproduce the TOCTOU gap Finding 1 describes:
        // Directory.Exists must return true, but Directory.EnumerateFiles (or the
        // foreach's implicit MoveNext) must throw once enumeration actually runs.
        //
        // A dangling NTFS junction achieves this reliably: the junction reparse
        // point itself still exists (so Directory.Exists(junction) == true), but
        // its target has been deleted, so enumerating into it throws
        // DirectoryNotFoundException from inside the foreach - exactly the class
        // of exception that, before the fix, sits outside the try/catch.
        var target = Path.Combine(_dir, $"target-{Guid.NewGuid():N}");
        var junction = Path.Combine(_dir, $"junction-{Guid.NewGuid():N}");
        Directory.CreateDirectory(target);

        var mklink = Process.Start(new ProcessStartInfo(
            "cmd.exe", $"/c mklink /J \"{junction}\" \"{target}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        mklink.WaitForExit();
        Assert.Equal(0, mklink.ExitCode);

        Directory.Delete(target, recursive: true); // leaves the junction dangling
        Assert.True(Directory.Exists(junction), "precondition: dangling junction must still report as existing");

        try
        {
            var records = new SessionRegistry(junction).ReadAll();
            Assert.Empty(records);
        }
        finally
        {
            // Note: The setup steps (Directory.CreateDirectory, mklink, and Directory.Delete above)
            // are not wrapped in try/finally. That is safe: if an exception fires before we reach
            // the finally, the test's Dispose() will clean up the temp directory recursively.
            // Directory.Delete(..., recursive: true) unlinks reparse points rather than following them,
            // so no orphaned junction or runaway recursion can result.
            try { Directory.Delete(junction); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void ReadAll_SkipsRecord_WhenTranscriptPathIsMissing()
    {
        WriteSession("good", $$"""
        {"sessionId":"good","transcriptPath":"p","pid":1,"pidStartUnixMs":0,"touchedUnixMs":{{FreshTouched}}}
        """);
        WriteSession("no-transcript", $$"""
        {"sessionId":"no-transcript","pid":1,"pidStartUnixMs":0,"touchedUnixMs":{{FreshTouched}}}
        """);

        var records = new SessionRegistry(_dir).ReadAll();

        Assert.Single(records);
        Assert.Equal("good", records[0].SessionId);
    }

    [Fact]
    public void ReadAll_SkipsRecord_WhenSessionIdIsMissing()
    {
        WriteSession("good2", $$"""
        {"sessionId":"good2","transcriptPath":"p","pid":1,"pidStartUnixMs":0,"touchedUnixMs":{{FreshTouched}}}
        """);
        WriteSession("no-session-id", $$"""
        {"transcriptPath":"p","pid":1,"pidStartUnixMs":0,"touchedUnixMs":{{FreshTouched}}}
        """);

        var records = new SessionRegistry(_dir).ReadAll();

        Assert.Single(records);
        Assert.Equal("good2", records[0].SessionId);
    }

    // SessionEnd 훅은 강제 종료(크래시, kill)에서는 안 불린다 — 그런 세션의 레코드는
    // 영영 지워지지 않고 매초 ReadAll에 계속 남는다. ReadAll이 매초 도는 유일한
    // 청소부이므로, 여기서 TTL을 넘긴 orphan을 직접 지운다.

    private const long DayMs = 24L * 60 * 60 * 1000;

    [Fact]
    public void ReadAll_DeletesOrphanedRecord_WhenTouchedUnixMsIsOlderThan7Days()
    {
        var now = 1_000_000_000_000L;
        var touched = now - 8 * DayMs;
        var path = Path.Combine(_dir, "orphan.json");
        WriteSession("orphan", $$"""
        {"sessionId":"orphan","transcriptPath":"p","pid":1,"pidStartUnixMs":0,"touchedUnixMs":{{touched}}}
        """);

        var records = new SessionRegistry(_dir).ReadAll(now);

        Assert.Empty(records);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ReadAll_KeepsRecord_WhenTouchedUnixMsIsWithin7Days()
    {
        var now = 1_000_000_000_000L;
        var touched = now - 60L * 60 * 1000; // 1시간 전
        var path = Path.Combine(_dir, "fresh.json");
        WriteSession("fresh", $$"""
        {"sessionId":"fresh","transcriptPath":"p","pid":1,"pidStartUnixMs":0,"touchedUnixMs":{{touched}}}
        """);

        var records = new SessionRegistry(_dir).ReadAll(now);

        Assert.Single(records);
        Assert.Equal("fresh", records[0].SessionId);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void ReadAll_DeletesCorruptFile_WhenMtimeIsOlderThan7Days()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var path = Path.Combine(_dir, "corrupt-old.json");
        WriteSession("corrupt-old", "{ this is not json");
        File.SetLastWriteTimeUtc(path, DateTimeOffset.FromUnixTimeMilliseconds(now - 8 * DayMs).UtcDateTime);

        var records = new SessionRegistry(_dir).ReadAll(now);

        Assert.Empty(records);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ReadAll_KeepsCorruptFile_WhenMtimeIsFresh()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var path = Path.Combine(_dir, "corrupt-fresh.json");
        WriteSession("corrupt-fresh", "{ this is not json");
        // 방금 쓴 파일이라 mtime은 이미 신선하다 — 별도 설정 불필요.

        var records = new SessionRegistry(_dir).ReadAll(now);

        Assert.Empty(records);
        Assert.True(File.Exists(path));
    }
}
