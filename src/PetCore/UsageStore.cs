using System.Text.Json;

namespace PetCore;

/// <summary>
/// 상태 파일을 읽고 쓴다. Load 도 Save 도 절대 던지지 않는다 — 레벨 표시가 실패했다고
/// 장식용 펫이 죽어서는 안 된다.
/// </summary>
public sealed class UsageStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private readonly string _dataDir;

    public UsageStore(string dataDir) => _dataDir = dataDir;

    private string Path_ => Path.Combine(_dataDir, "usage.json");

    /// <summary>읽을 수 없거나 형식이 다르면 빈 상태를 돌려준다. 전량 재스캔은 1초면 된다.</summary>
    public UsageState Load()
    {
        try
        {
            if (!File.Exists(Path_)) return new UsageState();

            using var stream = new FileStream(
                Path_, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            var state = JsonSerializer.Deserialize<UsageState>(stream, Options);

            if (state is null || state.Version != UsageState.CurrentVersion)
                return new UsageState();

            state.Files ??= new Dictionary<string, UsageFileEntry>(StringComparer.OrdinalIgnoreCase);
            return state;
        }
        catch (Exception)
        {
            return new UsageState();
        }
    }

    /// <summary>임시 파일에 쓴 뒤 옮긴다. 도중에 죽어도 반쯤 쓰인 파일이 남지 않는다.</summary>
    public void Save(UsageState state)
    {
        try
        {
            Directory.CreateDirectory(_dataDir);

            var temp = Path_ + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, Options));
            File.Move(temp, Path_, overwrite: true);
        }
        catch (Exception)
        {
            // 저장 실패는 다음 주기에 다시 시도된다. 그 사이에는 메모리의 값을 쓴다.
        }
    }
}
