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
            //
            // 여기서 던진 예외를 이 안에서 삼키지 않는다 — 삼켜서 files 를 빈 목록으로
            // 만들면 "디렉터리가 진짜 비어 있음"과 "열거가 실패해서 뭐가 있는지 모름"이
            // 구분되지 않는다. 후자를 전자처럼 취급하면 total 이 0으로, level 이 1로
            // 떨어지고 그 값이 그대로 저장되어 캐시 전체가 날아간다. 그래서 여기서는
            // 잡지 않고 바깥의 catch(Exception) 으로 넘겨, 그 사이클을 통째로 버리고
            // (저장하지 않고) 직전 스냅샷을 그대로 돌려주게 한다.
            var files = Directory.Exists(_projectsRoot)
                ? Directory.EnumerateFiles(_projectsRoot, "*.jsonl", SearchOption.AllDirectories).ToList()
                : new List<string>();

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

            // 이번 사이클에 stat 이 실패했거나 열거가 조용히 건너뛴 경로는, 파일이 아직
            // 존재하는 한 직전 비용을 그대로 들고 간다. 그러지 않으면 일시적 실패가 총액을
            // 떨어뜨린 채로 저장되고, 복구되는 순간 가짜 레벨업이 터진다. 진짜로 사라진
            // 파일은 File.Exists 가 false 이므로 여기서 복구되지 않고 그대로 빠진다
            // (DisappearedFilesDropOutOfTheTotal).
            foreach (var (path, cached) in state.Files)
            {
                if (fresh.ContainsKey(path)) continue;
                try { if (File.Exists(path)) fresh[path] = cached; } catch (Exception) { }
            }

            // 같은 트랜스크립트가 두 프로젝트 디렉터리에 바이트 단위로 복사되는 경우
            // (git worktree 세션) (크기, mtime) 이 같은 파일은 한 번만 총액에 더한다.
            // 어느 경로가 "대표"가 될지는 사이클마다 흔들리면 안 되므로 경로를 정렬해
            // 결정적으로 고른다. 캐시(fresh, state.Files)에는 두 파일 모두 그대로
            // 남아서 다음 사이클에 재스캔되지 않는다 — 총액 집계에서만 한 번으로 친다.
            var total = 0m;
            var countedKeys = new HashSet<(long Size, long MtimeUnixMs)>();
            foreach (var path in fresh.Keys.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var entry = fresh[path];
                if (!countedKeys.Add((entry.Size, entry.MtimeUnixMs))) continue;
                total += entry.CostUsd;
            }

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
