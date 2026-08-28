using PetCore;
using Xunit;

public class TranscriptParserTests
{
    [Fact]
    public void ParsesToolUse_WithToolName()
    {
        var line = """
        {"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Read","input":{}}]}}
        """;

        var events = TranscriptParser.ParseLine(line);

        var e = Assert.Single(events);
        Assert.Equal(TranscriptEventKind.ToolUse, e.Kind);
        Assert.Equal("Read", e.ToolName);
    }

    [Fact]
    public void ParsesToolResult_WithErrorFlag()
    {
        var line = """
        {"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","is_error":true}]}}
        """;

        var e = Assert.Single(TranscriptParser.ParseLine(line));
        Assert.Equal(TranscriptEventKind.ToolResult, e.Kind);
        Assert.True(e.IsError);
    }

    [Fact]
    public void ParsesAssistantText_WhenContentIsPlainString()
    {
        var line = """
        {"type":"assistant","message":{"role":"assistant","content":"done"}}
        """;

        var e = Assert.Single(TranscriptParser.ParseLine(line));
        Assert.Equal(TranscriptEventKind.AssistantText, e.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{not json")]
    [InlineData("{\"type\":\"summary\",\"summary\":\"x\"}")]
    public void ReturnsEmpty_ForUnparseableOrIrrelevantLines(string line)
    {
        Assert.Empty(TranscriptParser.ParseLine(line));
    }
}
