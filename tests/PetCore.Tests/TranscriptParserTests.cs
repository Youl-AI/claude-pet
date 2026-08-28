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

    [Theory]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("[1,2,3]")]
    [InlineData("\"x\"")]
    [InlineData("true")]
    public void ReturnsEmpty_WhenRootIsNotAnObject(string line)
    {
        Assert.Empty(TranscriptParser.ParseLine(line));
    }

    [Theory]
    [InlineData("""{"message":"hi"}""")]
    [InlineData("""{"message":null}""")]
    public void ReturnsEmpty_WhenMessageIsNotAnObject(string line)
    {
        Assert.Empty(TranscriptParser.ParseLine(line));
    }

    [Theory]
    [InlineData("""{"message":{"content":[1,2,3]}}""")]
    [InlineData("""{"message":{"content":[null]}}""")]
    public void ReturnsEmpty_WhenContentArrayItemsAreNotObjects(string line)
    {
        Assert.Empty(TranscriptParser.ParseLine(line));
    }

    [Fact]
    public void ReturnsEmpty_WhenItemTypeIsNotAString()
    {
        var line = """{"message":{"content":[{"type":123}]}}""";

        Assert.Empty(TranscriptParser.ParseLine(line));
    }

    [Fact]
    public void SkipsBadItems_ButStillReturnsGoodItemFromSameContentArray()
    {
        var line = """
        {"message":{"content":[{"type":123},{"type":"tool_use","id":"t1","name":"Read","input":{}}]}}
        """;

        var e = Assert.Single(TranscriptParser.ParseLine(line));
        Assert.Equal(TranscriptEventKind.ToolUse, e.Kind);
        Assert.Equal("Read", e.ToolName);
    }

    [Fact]
    public void ToolUse_WithNonStringName_DoesNotThrow_AndYieldsNullToolName()
    {
        var line = """{"message":{"content":[{"type":"tool_use","name":123}]}}""";

        var e = Assert.Single(TranscriptParser.ParseLine(line));
        Assert.Equal(TranscriptEventKind.ToolUse, e.Kind);
        Assert.Null(e.ToolName);
    }

    // --- role 기반 필터링 (finding 2) ---

    [Fact]
    public void UserRoleWithPlainStringContent_DoesNotProduceTurnEnd()
    {
        // 사용자가 첫 프롬프트를 입력한 줄. Claude가 아직 아무것도 안 했는데
        // 이걸 AssistantText(턴 종료)로 오인하면 안 된다.
        var line = """
        {"type":"user","message":{"role":"user","content":"fix this"}}
        """;

        Assert.Empty(TranscriptParser.ParseLine(line));
    }

    [Fact]
    public void UserRoleWithTextContentItem_DoesNotProduceTurnEnd()
    {
        var line = """
        {"type":"user","message":{"role":"user","content":[{"type":"text","text":"fix this"}]}}
        """;

        Assert.Empty(TranscriptParser.ParseLine(line));
    }

    [Fact]
    public void AssistantRoleWithTextContentItem_StillProducesAssistantText()
    {
        var line = """
        {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"done"}]}}
        """;

        var e = Assert.Single(TranscriptParser.ParseLine(line));
        Assert.Equal(TranscriptEventKind.AssistantText, e.Kind);
    }

    [Fact]
    public void AssistantRoleWithThinkingContentItem_StillProducesThinking()
    {
        var line = """
        {"type":"assistant","message":{"role":"assistant","content":[{"type":"thinking","thinking":"hmm"}]}}
        """;

        var e = Assert.Single(TranscriptParser.ParseLine(line));
        Assert.Equal(TranscriptEventKind.Thinking, e.Kind);
    }

    [Fact]
    public void UserRoleWithToolResultContentItem_StillParsesCorrectly()
    {
        // tool_result 는 원래 user 역할 줄에 실린다 — role 필터링이 이걸 깨면 안 된다.
        var line = """
        {"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","is_error":false}]}}
        """;

        var e = Assert.Single(TranscriptParser.ParseLine(line));
        Assert.Equal(TranscriptEventKind.ToolResult, e.Kind);
        Assert.False(e.IsError);
    }

    [Fact]
    public void AssistantRoleWithToolUseContentItem_StillParsesCorrectly()
    {
        // tool_use 는 원래 assistant 역할 줄에 실린다 — role 필터링이 이걸 깨면 안 된다.
        var line = """
        {"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{}}]}}
        """;

        var e = Assert.Single(TranscriptParser.ParseLine(line));
        Assert.Equal(TranscriptEventKind.ToolUse, e.Kind);
        Assert.Equal("Bash", e.ToolName);
    }
}
