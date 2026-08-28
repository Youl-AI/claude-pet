namespace PetCore;

/// <summary>
/// 누적 비용(API 환산 달러)을 레벨로 바꾼다. 스펙 §4.
///
/// 구간 1은 로그, 구간 2·3은 선형이다. 구간 2를 로그로 두면 L100 을 넘는 순간 레벨 값이
/// 23배 싸진다 — 구간 2는 레벨이 900개인데 비용 범위는 구간 1보다 좁기 때문이다. 한 달
/// 걸려 L100 을 찍자마자 L101 부터 우수수 오르는 모양이 되므로 선형을 쓴다.
/// </summary>
public static class LevelCurve
{
    // --- 정본 상수. 이 넷만 손으로 정한다. ---
    public const double A  = 13.6869;   // 구간 1 로그 계수
    public const double C0 = 3.61;      // L1 문턱
    public const double C2 = 4992.0;    // L100 (= 기준 사용자 한 달)
    public const double M3 = 1396.17;   // 구간 2 레벨당 달러

    // --- 유도 상수. 하드코딩하지 않는다 (스펙 §4.1). ---
    public static double M4 => M3 * 1.9;
    public static double C3 => C2 + 900 * M3;
    public static double C4 => C3 + 8999 * M4;

    public const int MinLevel = 1;
    public const int MaxLevel = 9999;

    public static int LevelFor(decimal costUsd)
    {
        if (costUsd <= 0m) return MinLevel;

        var c = (double)costUsd;

        double level;
        if (c < C0)       level = MinLevel;
        else if (c < C2)  level = 1 + A * Math.Log(c / C0);
        else if (c < C3)  level = 100 + (c - C2) / M3;
        else              level = 1000 + (c - C3) / M4;

        // 부동소수 오차로 경계에서 99.9999 가 나오면 내림 때문에 99 가 된다. 앵커가
        // 정확히 떨어지도록 아주 작은 값을 더한 뒤 내림한다.
        var rounded = (int)Math.Floor(level + 1e-9);

        return Math.Clamp(rounded, MinLevel, MaxLevel);
    }
}
