using System.Text.Json;

namespace PetCore;

public sealed class SessionRegistry
{
    private static readonly JsonSerializerOptions Options =
        new() { PropertyNameCaseInsensitive = true };

    // SessionEnd 훅은 강제 종료(크래시, taskkill)에서는 호출되지 않는다 — 그런 세션의
    // 레코드는 아무도 지우지 않으면 sessions/*.json 아래에 영원히 남아 매초 ReadAll이
    // 역직렬화한다. ReadAll이 1Hz로 도는 유일한 청소부이므로, 여기서 직접 orphan을
    // 정리한다.
    private const long OrphanTtlMs = 7L * 24 * 60 * 60 * 1000;

    private readonly string _directory;

    public SessionRegistry(string directory) => _directory = directory;

    public IReadOnlyList<SessionRecord> ReadAll(long? nowUnixMs = null)
    {
        var now = nowUnixMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var records = new List<SessionRecord>();

        // ReadAll()의 계약은 "절대 던지지 않는다"이다. 이 디렉터리는 여러 PowerShell
        // 훅 스크립트가 동시에 쓰고 지우는 대상이라, Directory.Exists 확인 이후에도
        // 디렉터리가 사라지거나, 열거 도중 파일이 삭제되거나, 권한이 바뀌는 등
        // 다양한 예외(DirectoryNotFoundException, IOException,
        // UnauthorizedAccessException, PathTooLongException, ...)가 열거/파일 열기/
        // 역직렬화 어느 단계에서든 터질 수 있다. 개별 예외 타입을 하나씩 잡아내다
        // 다음 것을 놓치는 대신, 이 메서드 경계 전체를 catch-all로 감싸 구조적으로
        // "절대 던지지 않음"을 보장한다. 이 메서드를 호출하는 쪽은 예외로 할 수 있는
        // 일이 없고, 손상된 파일을 건너뛰는 것 자체가 명세된 동작이다.
        try
        {
            if (!Directory.Exists(_directory))
                return records;

            foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
            {
                try
                {
                    using var stream = new FileStream(
                        file, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);

                    var record = JsonSerializer.Deserialize<SessionRecord>(stream, Options);
                    if (record is not null
                        && !string.IsNullOrEmpty(record.SessionId)
                        && !string.IsNullOrEmpty(record.TranscriptPath))
                    {
                        // TouchedUnixMs가 TTL을 넘겼으면 orphan이다 — 결과에 넣지 않고
                        // 파일도 지운다. SessionEnd가 안 불린 크래시 세션이 sessions/
                        // 아래에 영원히 쌓이는 것을 막는 유일한 지점이다.
                        if (now - record.TouchedUnixMs > OrphanTtlMs)
                        {
                            TryDelete(file);
                        }
                        else
                        {
                            records.Add(record);
                        }
                    }
                }
                catch
                {
                    // 개별 파일 하나가 손상되었거나, 훅이 쓰는 중이거나, 그 사이에
                    // 지워졌을 수 있다. 그 파일만 건너뛰고 나머지는 계속 읽는다.
                    // 다만 손상된 채로 mtime이 TTL을 넘겼다면 — 아무도 다시 쓰지 않을
                    // 죽은 파일이라는 뜻이다 — 역시 지운다. 파싱할 수 없는 레코드가
                    // 영원히 남아 있을 이유는 없다.
                    TryDeleteIfStale(file, now);
                }
            }
        }
        catch
        {
            // 디렉터리 자체가 열거 도중 사라지거나 접근할 수 없게 된 경우 등.
            // 지금까지 모은 레코드만 반환하고, 다음 폴링에서 다시 시도한다.
        }

        return records;
    }

    // 삭제 실패는 무시한다 — 다음 폴링에서 다시 시도한다. 청소는 부가 동작이지
    // ReadAll의 "절대 던지지 않는다" 계약을 깰 이유가 못 된다.
    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception)
        {
        }
    }

    // 파싱에 실패한 파일은 mtime으로만 나이를 판단할 수 있다. 신선하면(예: 훅이
    // 아직 다 쓰는 중) 건드리지 않고 다음 폴링에서 다시 읽어본다.
    private static void TryDeleteIfStale(string file, long now)
    {
        try
        {
            var mtime = File.GetLastWriteTimeUtc(file);
            var mtimeUnixMs = new DateTimeOffset(mtime, TimeSpan.Zero).ToUnixTimeMilliseconds();
            if (now - mtimeUnixMs > OrphanTtlMs)
            {
                File.Delete(file);
            }
        }
        catch (Exception)
        {
        }
    }
}
