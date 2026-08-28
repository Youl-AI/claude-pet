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
        // 방해 0 검증: 펫이 읽는 동안 Claude Code가 같은 파일에 쓸 수 있어야 한다.
        File.WriteAllText(_path, ToolUseLine + "\n");
        var tail = new TranscriptTail(_path);
        tail.ReadNew();

        using var writer = new FileStream(
            _path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var bytes = System.Text.Encoding.UTF8.GetBytes(ToolUseLine + "\n");

        var ex = Record.Exception(() =>
        {
            writer.Write(bytes, 0, bytes.Length);
            writer.Flush();
            tail.ReadNew();
        });

        Assert.Null(ex);
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
}
