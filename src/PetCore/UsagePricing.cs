namespace PetCore;

/// <summary>
/// 한 번의 API 호출이 소비한 토큰 네 종류. 트랜스크립트의 message.usage 와 1:1 대응한다.
/// </summary>
public readonly record struct TokenCounts(
    long Input,
    long CacheCreation,
    long CacheRead,
    long Output);

/// <summary>
/// 토큰을 API 환산 달러로 바꾼다.
///
/// raw 토큰 합계를 쓰지 않는 이유는 스펙 §2.2에 있다 — 실측상 cache_read 가 전체 토큰의
/// 97.7%인데 output 대비 1/50 가격이다. 합계로 세면 일한 양이 아니라 캐시 읽기를 세는 것이고,
/// 긴 세션을 열어두는 것만으로 레벨이 오르는 파밍 경로가 열린다.
/// </summary>
public static class UsagePricing
{
    /// <summary>캐시 쓰기는 input 단가의 1.25배.</summary>
    private const decimal CacheWriteMultiplier = 1.25m;

    /// <summary>캐시 읽기는 input 단가의 0.10배.</summary>
    private const decimal CacheReadMultiplier = 0.10m;

    private const decimal PerMillion = 1_000_000m;

    private readonly record struct Rate(decimal InputPerMillion, decimal OutputPerMillion);

    private static readonly Rate Fallback = new(5.00m, 25.00m);   // claude-opus-5

    private static readonly Dictionary<string, Rate> Rates =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-opus-5"]              = new(5.00m, 25.00m),
            ["claude-opus-4-8"]            = new(5.00m, 25.00m),
            ["claude-fable-5"]             = new(10.00m, 50.00m),
            ["claude-sonnet-5"]            = new(2.00m, 10.00m),
            ["claude-haiku-4-5-20251001"]  = new(1.00m, 5.00m),
        };

    public static decimal CostUsd(string? model, TokenCounts tokens)
    {
        // 가격표에 없는 모델은 Opus 5 단가로 센다. 새 모델이 나왔을 때 레벨이 멈추는 것보다
        // 과대/과소 추정이 낫다 (스펙 §2.3).
        var rate = model is not null && Rates.TryGetValue(model, out var found) ? found : Fallback;

        var input = tokens.Input * rate.InputPerMillion;
        var write = tokens.CacheCreation * rate.InputPerMillion * CacheWriteMultiplier;
        var read  = tokens.CacheRead * rate.InputPerMillion * CacheReadMultiplier;
        var output = tokens.Output * rate.OutputPerMillion;

        return (input + write + read + output) / PerMillion;
    }
}
