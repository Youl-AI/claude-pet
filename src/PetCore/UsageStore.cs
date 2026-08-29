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

            // JsonSerializer.Deserialize 는 항상 기본(대소문자 구분) 비교자로 딕셔너리를
            // 만든다 — Files 필드에 초기값이 있어도 역직렬화가 그 자리를 새 딕셔너리로
            // 덮어쓰기 때문에 ??= 만으로는 부족하다. UsageTracker 의 fresh 는
            // OrdinalIgnoreCase 이므로, 여기서 다시 만들어 맞추지 않으면 저장된 경로와
            // 열거된 경로의 대소문자가 어긋나는 순간 캐시 조회가 전부 미스 나서 30초마다
            // 전체 트리를 영원히 재스캔하게 된다.
            state.Files = state.Files is null
                ? new Dictionary<string, UsageFileEntry>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, UsageFileEntry>(state.Files, StringComparer.OrdinalIgnoreCase);
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
