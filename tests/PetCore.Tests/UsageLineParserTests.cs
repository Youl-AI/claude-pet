using PetCore;

public class UsageLineParserTests
{
    private const string RealLine = """
    {"type":"assistant","uuid":"abc","message":{"id":"msg_011Ce5bQ","role":"assistant","model":"claude-opus-5","content":[{"type":"text","text":"hi"}],"usage":{"input_tokens":2,"cache_creation_input_tokens":933,"cache_read_input_tokens":469803,"output_tokens":920,"service_tier":"standard"}}}
    """;

    [Fact]
    public void ParsesAllFourTokenCountsAndTheModel()
    {
        Assert.True(UsageLineParser.TryParse(RealLine, out var r));

        Assert.Equal("msg_011Ce5bQ", r.MessageId);
        Assert.Equal("claude-opus-5", r.Model);
        Assert.Equal(2, r.Tokens.Input);
        Assert.Equal(933, r.Tokens.CacheCreation);
        Assert.Equal(469803, r.Tokens.CacheRead);
        Assert.Equal(920, r.Tokens.Output);
    }

    [Fact]
    public void RejectsLineWithoutUsage()
    {
        var line = """{"type":"user","message":{"role":"user","content":"hello"}}""";

        Assert.False(UsageLineParser.TryParse(line, out _));
    }

    [Fact]
    public void RejectsLineWithoutMessageId()
    {
        // id 가 없으면 중복 제거를 할 수 없다. 세지 않는 편이 두 번 세는 것보다 낫다.
        var line = """{"message":{"usage":{"input_tokens":5,"output_tokens":5}}}""";

        Assert.False(UsageLineParser.TryParse(line, out _));
    }

    [Fact]
    public void MissingTokenFieldsCountAsZero()
    {
        var line = """{"message":{"id":"msg_x","usage":{"output_tokens":7}}}""";

        Assert.True(UsageLineParser.TryParse(line, out var r));
        Assert.Equal(0, r.Tokens.Input);
        Assert.Equal(0, r.Tokens.CacheRead);
        Assert.Equal(7, r.Tokens.Output);
    }

    [Fact]
    public void MissingModelIsNull()
    {
        var line = """{"message":{"id":"msg_x","usage":{"output_tokens":1}}}""";

        Assert.True(UsageLineParser.TryParse(line, out var r));
        Assert.Null(r.Model);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"message\":")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"message\":\"a string, not an object\"}")]
    [InlineData("{\"message\":{\"usage\":\"also a string\"}}")]
    [InlineData("{\"message\":{\"id\":123,\"usage\":{\"output_tokens\":1}}}")]
    public void MalformedInputReturnsFalseAndNeverThrows(string line)
    {
        Assert.False(UsageLineParser.TryParse(line, out _));
    }

    [Fact]
    public void NonNumericTokenValueIsTreatedAsZero()
    {
        var line = """{"message":{"id":"msg_x","usage":{"output_tokens":"lots"}}}""";

        Assert.True(UsageLineParser.TryParse(line, out var r));
        Assert.Equal(0, r.Tokens.Output);
    }
}
