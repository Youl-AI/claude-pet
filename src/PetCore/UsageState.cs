namespace PetCore;

/// <summary>트랜스크립트 파일 하나에 대해 기억해 두는 것.</summary>
public sealed class UsageFileEntry
{
    public long Size { get; set; }
    public long MtimeUnixMs { get; set; }
    public decimal CostUsd { get; set; }
}

/// <summary>
/// 상태 파일의 내용. 파일별 (크기, mtime, 비용)을 기억해 두었다가, 다음 기동 때 값이 그대로면
/// 그 파일을 아예 읽지 않는다 (스펙 §7.2).
/// </summary>
public sealed class UsageState
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public decimal TotalCostUsd { get; set; }
    public int Level { get; set; }
    public Dictionary<string, UsageFileEntry> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
