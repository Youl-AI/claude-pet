namespace PetCore;

/// <summary>한 번의 갱신 결과.</summary>
public readonly record struct UsageSnapshot(decimal TotalCostUsd, int Level, bool LeveledUp);

/// <summary>
/// 트랜스크립트 전체의 누적 비용과 레벨을 유지한다.
///
/// 605개 파일 1.1 GB 를 매번 읽지 않는다. 파일별 (크기, mtime)이 저장된 값과 같으면 읽지 않고
/// 저장된 비용을 그대로 쓴다. 실제로 바뀌는 것은 현재 세션 파일 하나뿐이다 (스펙 §7.2).
/// </summary>
public sealed class UsageTracker
{
    private readonly string _projectsRoot;
    private readonly UsageStore _store;
    private readonly TranscriptCostScanner _scanner;

    public UsageTracker(string projectsRoot, UsageStore store, TranscriptCostScanner scanner)
    {
        _projectsRoot = projectsRoot;
        _store = store;
        _scanner = scanner;
    }

    /// <summary>절대 던지지 않는다. 실패하면 직전 값(없으면 레벨 1)을 돌려준다.</summary>
    public UsageSnapshot Refresh()
    {
        var state = _store.Load();
        var previousLevel = state.Level;

        try
        {
            var fresh = new Dictionary<string, UsageFileEntry>(StringComparer.OrdinalIgnoreCase);

            // 열거 자체를 try 안에 둔다. EnumerateFiles 는 지연 평가라 MoveNext() 에서 던진다 —
            // foreach 를 try 밖에 두면 그 예외가 새어나간다. 이 실수는 이 저장소의
            // SessionRegistry 에서 이미 한 번 잡혔다.
            List<string> files;
            try
            {
                files = Directory.Exists(_projectsRoot)
                    ? Directory.EnumerateFiles(_projectsRoot, "*.jsonl", SearchOption.AllDirectories).ToList()
                    : new List<string>();
            }
            catch (Exception)
            {
                files = new List<string>();
            }

            foreach (var path in files)
            {
                try
                {
                    var info = new FileInfo(path);
                    var size = info.Length;
                    var mtime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeMilliseconds();

                    if (state.Files.TryGetValue(path, out var cached)
                        && cached.Size == size && cached.MtimeUnixMs == mtime)
                    {
                        fresh[path] = cached;     // 안 읽는다
                        continue;
                    }

                    fresh[path] = new UsageFileEntry
                    {
                        Size = size,
                        MtimeUnixMs = mtime,
                        CostUsd = _scanner.ScanFile(path),
                    };
                }
                catch (Exception)
                {
                    // 이 파일만 건너뛴다. 사라졌거나 권한이 바뀌었을 수 있다.
                }
            }

            var total = 0m;
            foreach (var entry in fresh.Values) total += entry.CostUsd;

            var level = LevelCurve.LevelFor(total);

            state.Version = UsageState.CurrentVersion;
            state.Files = fresh;
            state.TotalCostUsd = total;
            state.Level = level;
            _store.Save(state);

            // 처음 켰을 때(previousLevel == 0) 그동안 쌓인 레벨로 이펙트가 터지면 안 된다.
            var leveledUp = previousLevel > 0 && level > previousLevel;

            return new UsageSnapshot(total, level, leveledUp);
        }
        catch (Exception)
        {
            var fallbackLevel = previousLevel > 0 ? previousLevel : LevelCurve.MinLevel;
            return new UsageSnapshot(state.TotalCostUsd, fallbackLevel, false);
        }
    }
}
