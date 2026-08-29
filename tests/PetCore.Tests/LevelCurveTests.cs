using PetCore;

public class LevelCurveTests
{
    [Fact]
    public void DerivedConstantsAreComputedFromTheCanonicalFour()
    {
        // 스펙 §4.1: M4/C3/C4 를 하드코딩하면 M3 를 조정했을 때 경계가 따라오지 않는다.
        Assert.Equal(LevelCurve.M3 * 1.9, LevelCurve.M4, 6);
        Assert.Equal(LevelCurve.C2 + 900 * LevelCurve.M3, LevelCurve.C3, 6);
        Assert.Equal(LevelCurve.C3 + 8999 * LevelCurve.M4, LevelCurve.C4, 6);
    }

    [Fact]
    public void BelowTheFirstThreshold_ClampsToOne()
    {
        Assert.Equal(1, LevelCurve.LevelFor(0m));
        Assert.Equal(1, LevelCurve.LevelFor(1m));
        Assert.Equal(1, LevelCurve.LevelFor(3.60m));
    }

    [Fact]
    public void AtTheFirstThreshold_IsExactlyLevelOne()
    {
        Assert.Equal(1, LevelCurve.LevelFor((decimal)LevelCurve.C0));
    }

    [Fact]
    public void HitsTheDesignAnchors()
    {
        // 스펙 §4.2 의 두 앵커. $30 은 곡선을 맞출 때 쓴 근사 앵커라 원값이 29.9819 이고,
        // 내림하면 29 다 — 스펙 §4.3 의 페이스 표도 내림 기준이므로 일치한다.
        // $4,992 는 구간 경계라 정확히 100 이다.
        Assert.Equal(29, LevelCurve.LevelFor(30m));
        Assert.Equal(30, LevelCurve.LevelFor(30.10m));
        Assert.Equal(100, LevelCurve.LevelFor((decimal)LevelCurve.C2));
    }

    [Fact]
    public void HitsTheBandBoundaries()
    {
        Assert.Equal(1000, LevelCurve.LevelFor((decimal)LevelCurve.C3));
        Assert.Equal(9999, LevelCurve.LevelFor((decimal)LevelCurve.C4));
    }

    [Fact]
    public void AboveTheCeiling_ClampsTo9999()
    {
        Assert.Equal(9999, LevelCurve.LevelFor((decimal)LevelCurve.C4 * 10m));
    }

    [Fact]
    public void IsMonotonicAcrossBothBandBoundaries()
    {
        // 경계에서 레벨이 뒷걸음질치면 명패 숫자가 내려간다. 절대 그러면 안 된다.
        var previous = 0;
        foreach (var cost in new[] { 3.61m, 30m, 500m, 4991m, 4992m, 4993m,
                                     100_000m, 1_261_544m, 1_261_545m, 1_261_546m,
                                     5_000_000m, 25_133_372m })
        {
            var level = LevelCurve.LevelFor(cost);
            Assert.True(level >= previous, $"${cost} 에서 레벨이 {previous} → {level} 로 내려갔다");
            previous = level;
        }
    }

    [Fact]
    public void MatchesTheSpecPaceTable()
    {
        // 스펙 §4.3. 기준 사용자 $166.3947/일.
        const decimal perDay = 6323m / 38m;

        Assert.Equal(99, LevelCurve.LevelFor(perDay * 30));    // 1달
        Assert.Equal(139, LevelCurve.LevelFor(perDay * 365));  // 1년
        Assert.Equal(313, LevelCurve.LevelFor(perDay * 1825)); // 5년
    }

    [Fact]
    public void NegativeCostIsTreatedAsZero()
    {
        Assert.Equal(1, LevelCurve.LevelFor(-5m));
    }
}
