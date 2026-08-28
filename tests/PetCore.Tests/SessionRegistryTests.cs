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

    [Fact]
    public void ReadAll_ParsesWellFormedRecords()
    {
        WriteSession("abc", """
        {"sessionId":"abc","transcriptPath":"C:\\t\\abc.jsonl","pid":1234,"pidStartUnixMs":111,"touchedUnixMs":222}
        """);

        var r = Assert.Single(new SessionRegistry(_dir).ReadAll());
        Assert.Equal("abc", r.SessionId);
        Assert.Equal(1234, r.Pid);
        Assert.Equal(111, r.PidStartUnixMs);
    }

    [Fact]
    public void ReadAll_SkipsCorruptFilesInsteadOfThrowing()
    {
        WriteSession("good", """
        {"sessionId":"good","transcriptPath":"p","pid":1,"pidStartUnixMs":0,"touchedUnixMs":0}
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
            try { Directory.Delete(junction); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void ReadAll_SkipsRecord_WhenTranscriptPathIsMissing()
    {
        WriteSession("good", """
        {"sessionId":"good","transcriptPath":"p","pid":1,"pidStartUnixMs":0,"touchedUnixMs":0}
        """);
        WriteSession("no-transcript", """
        {"sessionId":"no-transcript","pid":1,"pidStartUnixMs":0,"touchedUnixMs":0}
        """);

        var records = new SessionRegistry(_dir).ReadAll();

        Assert.Single(records);
        Assert.Equal("good", records[0].SessionId);
    }

    [Fact]
    public void ReadAll_SkipsRecord_WhenSessionIdIsMissing()
    {
        WriteSession("good2", """
        {"sessionId":"good2","transcriptPath":"p","pid":1,"pidStartUnixMs":0,"touchedUnixMs":0}
        """);
        WriteSession("no-session-id", """
        {"transcriptPath":"p","pid":1,"pidStartUnixMs":0,"touchedUnixMs":0}
        """);

        var records = new SessionRegistry(_dir).ReadAll();

        Assert.Single(records);
        Assert.Equal("good2", records[0].SessionId);
    }
}
