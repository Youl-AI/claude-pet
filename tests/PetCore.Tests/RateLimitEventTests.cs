using PetCore;

public class RateLimitEventTests
{
    // 실측 형식 (스펙 §1.1). usage 토큰은 전부 0으로 온다.
    private const string SessionLimitLine =
        """{"type":"assistant","uuid":"x","message":{"id":"msg_rl1","role":"assistant","model":"claude-opus-5","content":[{"type":"text","text":"You've hit your session limit · resets 6:10pm (Asia/Seoul)"}],"usage":{"input_tokens":0,"output_tokens":0}},"error":"rate_limit","isApiErrorMessage":true}""";

    private const string MonthlyLimitLine =
        """{"type":"assistant","uuid":"x","message":{"id":"msg_rl2","role":"assistant","model":"claude-opus-5","content":[{"type":"text","text":"You've hit your monthly spend limit · raise it at claude.ai/settings"}],"usage":{"input_tokens":0,"output_tokens":0}},"error":"rate_limit","isApiErrorMessage":true}""";

    private const string AuthErrorLine =
        """{"type":"assistant","uuid":"x","message":{"id":"msg_ae","role":"assistant","content":[{"type":"text","text":"OAuth access token has expired."}]},"error":"authentication_failed","isApiErrorMessage":true}""";

    [Fact]
    public void RateLimitLineBecomesOneRateLimitedEvent()
    {
        var events = TranscriptParser.ParseLine(SessionLimitLine);

        var e = Assert.Single(events);
        Assert.Equal(TranscriptEventKind.RateLimited, e.Kind);
    }

    [Fact]
    public void RateLimitLineIsNotMisreadAsTurnEnd()
    {
        // 이 줄은 assistant text 형태라 기존 규칙대로면 AssistantText(턴 종료)가 된다.
        var events = TranscriptParser.ParseLine(SessionLimitLine);
        Assert.DoesNotContain(events, e => e.Kind == TranscriptEventKind.AssistantText);
    }

    [Fact]
    public void SessionLimitCarriesTheResetInstant()
    {
        var e = Assert.Single(TranscriptParser.ParseLine(SessionLimitLine));
        Assert.NotNull(e.ResetAtUnixMs);

        // ParseLine 내부의 '지금'은 주입할 수 없으므로 정확한 값 대신 범위로 확인한다:
        // 리셋은 지금 이후, 24시간 이내여야 한다 (과거면 다음 날로 넘긴다는 규칙의 귀결).
        var now = DateTimeOffset.Now;
        Assert.InRange(e.ResetAtUnixMs.Value,
            now.ToUnixTimeMilliseconds(),
            now.AddHours(24).ToUnixTimeMilliseconds());
    }

    [Fact]
    public void MonthlyLimitHasNoResetInstantButStillSleeps()
    {
        var e = Assert.Single(TranscriptParser.ParseLine(MonthlyLimitLine));
        Assert.Equal(TranscriptEventKind.RateLimited, e.Kind);
        Assert.Null(e.ResetAtUnixMs);
    }

    [Fact]
    public void OtherApiErrorsAreNotRateLimited()
    {
        var events = TranscriptParser.ParseLine(AuthErrorLine);
        Assert.DoesNotContain(events, e => e.Kind == TranscriptEventKind.RateLimited);
    }

    // --- RateLimitReset.Resolve: 시계를 주입하는 순수 함수 ---

    private static readonly DateTimeOffset Noon =
        new(2026, 8, 29, 12, 0, 0, TimeSpan.FromHours(9));

    [Fact]
    public void ResolvesAFutureTimeToday()
    {
        var ms = RateLimitReset.Resolve("You've hit your session limit · resets 6:10pm (Asia/Seoul)", Noon);
        Assert.Equal(new DateTimeOffset(2026, 8, 29, 18, 10, 0, TimeSpan.FromHours(9)).ToUnixTimeMilliseconds(), ms);
    }

    [Fact]
    public void RollsAPastTimeToTomorrow()
    {
        // 정오에 "resets 2:20am"을 보면 내일 새벽이다.
        var ms = RateLimitReset.Resolve("You've hit your session limit · resets 2:20am (Asia/Seoul)", Noon);
        Assert.Equal(new DateTimeOffset(2026, 8, 30, 2, 20, 0, TimeSpan.FromHours(9)).ToUnixTimeMilliseconds(), ms);
    }

    [Fact]
    public void TwelveOclockEdgesResolveCorrectly()
    {
        // 12am = 0시, 12pm = 12시. 12시간제 파싱의 고전적 함정.
        var am = RateLimitReset.Resolve("resets 12:05am (Asia/Seoul)", Noon);
        Assert.Equal(new DateTimeOffset(2026, 8, 30, 0, 5, 0, TimeSpan.FromHours(9)).ToUnixTimeMilliseconds(), am);

        var pm = RateLimitReset.Resolve("resets 12:30pm (Asia/Seoul)", Noon);
        Assert.Equal(new DateTimeOffset(2026, 8, 29, 12, 30, 0, TimeSpan.FromHours(9)).ToUnixTimeMilliseconds(), pm);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("You've hit your monthly spend limit · raise it at claude.ai")]
    [InlineData("resets soon")]
    [InlineData("resets 25:99xx")]
    [InlineData("resets 13:30pm (Asia/Seoul)")]
    public void UnparseableTextYieldsNull(string? text)
    {
        Assert.Null(RateLimitReset.Resolve(text, Noon));
    }
}
