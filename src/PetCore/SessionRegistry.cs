using System.Text.Json;

namespace PetCore;

public sealed class SessionRegistry
{
    private static readonly JsonSerializerOptions Options =
        new() { PropertyNameCaseInsensitive = true };

    private readonly string _directory;

    public SessionRegistry(string directory) => _directory = directory;

    public IReadOnlyList<SessionRecord> ReadAll()
    {
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
                        records.Add(record);
                    }
                }
                catch
                {
                    // 개별 파일 하나가 손상되었거나, 훅이 쓰는 중이거나, 그 사이에
                    // 지워졌을 수 있다. 그 파일만 건너뛰고 나머지는 계속 읽는다.
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
}
