using PetCore;

public class UsageStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pet-store-" + Guid.NewGuid().ToString("N"));

    public UsageStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string StatePath => Path.Combine(_dir, "usage.json");

    [Fact]
    public void LoadOnAnEmptyDirectoryReturnsAFreshState()
    {
        var state = new UsageStore(_dir).Load();

        Assert.Equal(UsageState.CurrentVersion, state.Version);
        Assert.Equal(0m, state.TotalCostUsd);
        Assert.Equal(0, state.Level);
        Assert.Empty(state.Files);
    }

    [Fact]
    public void SaveThenLoadRoundTrips()
    {
        var store = new UsageStore(_dir);
        var state = new UsageState { TotalCostUsd = 6323.23m, Level = 99 };
        state.Files["C:/x/a.jsonl"] = new UsageFileEntry { Size = 20695541, MtimeUnixMs = 1756400000000, CostUsd = 412.83m };

        store.Save(state);
        var loaded = store.Load();

        Assert.Equal(6323.23m, loaded.TotalCostUsd);
        Assert.Equal(99, loaded.Level);
        var entry = Assert.Single(loaded.Files);
        Assert.Equal("C:/x/a.jsonl", entry.Key);
        Assert.Equal(20695541, entry.Value.Size);
        Assert.Equal(1756400000000, entry.Value.MtimeUnixMs);
        Assert.Equal(412.83m, entry.Value.CostUsd);
    }

    [Fact]
    public void LoadedFilesDictionaryIsCaseInsensitiveEvenAfterRoundTrippingThroughJson()
    {
        // UsageTracker 는 fresh 를 OrdinalIgnoreCase 로 만든다. state.Files 가
        // JsonSerializer.Deserialize 직후 기본(대소문자 구분) 비교자로 오면, 저장된
        // 경로와 열거된 경로의 대소문자가 어긋나는 순간 캐시 조회가 전부 미스 나서
        // 30초마다 전체 트리를 영원히 재스캔하게 된다.
        var store = new UsageStore(_dir);
        var state = new UsageState();
        state.Files["C:/x/a.jsonl"] = new UsageFileEntry { Size = 1, MtimeUnixMs = 2, CostUsd = 3m };
        store.Save(state);

        var loaded = store.Load();

        Assert.True(loaded.Files.ContainsKey("c:/X/A.JSONL"));
    }

    [Fact]
    public void CorruptFileYieldsAFreshStateInsteadOfThrowing()
    {
        File.WriteAllText(StatePath, "{ this is not json");

        var state = new UsageStore(_dir).Load();

        Assert.Equal(0m, state.TotalCostUsd);
        Assert.Empty(state.Files);
    }

    [Fact]
    public void UnknownVersionYieldsAFreshState()
    {
        // 형식이 바뀌면 옛 파일을 해석하려 들지 말고 다시 스캔한다. 1초면 된다.
        File.WriteAllText(StatePath, """{"version":999,"totalCostUsd":1,"level":1,"files":{}}""");

        var state = new UsageStore(_dir).Load();

        Assert.Equal(UsageState.CurrentVersion, state.Version);
        Assert.Equal(0m, state.TotalCostUsd);
    }

    [Fact]
    public void SaveCreatesTheDirectoryIfItIsMissing()
    {
        var nested = Path.Combine(_dir, "does", "not", "exist");

        new UsageStore(nested).Save(new UsageState { Level = 7 });

        Assert.Equal(7, new UsageStore(nested).Load().Level);
    }

    [Fact]
    public void SaveIsAtomic_NoTempFileIsLeftBehind()
    {
        var store = new UsageStore(_dir);
        store.Save(new UsageState { Level = 3 });

        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void SaveToAnUnwritablePathDoesNotThrow()
    {
        // 파일 이름이 될 수 없는 경로. 저장 실패가 펫을 죽여서는 안 된다.
        var store = new UsageStore(Path.Combine(_dir, "usage.json"));   // 파일을 디렉터리로 넘김
        File.WriteAllText(Path.Combine(_dir, "usage.json"), "x");

        store.Save(new UsageState { Level = 1 });   // 던지지 않으면 통과
    }
}
