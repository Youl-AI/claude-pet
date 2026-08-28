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
}
