using PetCore;

public class UsagePricingTests
{
    [Fact]
    public void Opus5_AppliesInputAndOutputRates()
    {
        // input 1M x $5 + output 1M x $25 = $30
        var cost = UsagePricing.CostUsd("claude-opus-5",
            new TokenCounts(1_000_000, 0, 0, 1_000_000));

        Assert.Equal(30.0m, cost);
    }

    [Fact]
    public void CacheWriteIs125PercentOfInput_CacheReadIs10Percent()
    {
        // opus-5 input $5/1M -> write $6.25/1M, read $0.50/1M
        var cost = UsagePricing.CostUsd("claude-opus-5",
            new TokenCounts(0, 1_000_000, 1_000_000, 0));

        Assert.Equal(6.75m, cost);
    }

    [Fact]
    public void SonnetIsCheaperThanOpus_ForTheSameTokens()
    {
        var tokens = new TokenCounts(1_000_000, 0, 0, 1_000_000);

        Assert.True(UsagePricing.CostUsd("claude-sonnet-5", tokens)
                  < UsagePricing.CostUsd("claude-opus-5", tokens));
    }

    [Fact]
    public void UnknownModel_FallsBackToOpus5Rates()
    {
        // 새 모델이 나와도 레벨이 멈추면 안 된다 (스펙 §2.3).
        var tokens = new TokenCounts(1_000_000, 0, 0, 0);

        Assert.Equal(UsagePricing.CostUsd("claude-opus-5", tokens),
                     UsagePricing.CostUsd("claude-something-unreleased-7", tokens));
    }

    [Fact]
    public void NullModel_FallsBackToOpus5Rates()
    {
        var tokens = new TokenCounts(1_000_000, 0, 0, 0);

        Assert.Equal(5.0m, UsagePricing.CostUsd(null, tokens));
    }

    [Fact]
    public void ModelNameIsCaseInsensitive()
    {
        var tokens = new TokenCounts(1_000_000, 0, 0, 0);

        Assert.Equal(UsagePricing.CostUsd("claude-sonnet-5", tokens),
                     UsagePricing.CostUsd("CLAUDE-SONNET-5", tokens));
    }

    [Fact]
    public void ZeroTokens_CostNothing()
    {
        Assert.Equal(0m, UsagePricing.CostUsd("claude-opus-5", new TokenCounts(0, 0, 0, 0)));
    }
}
