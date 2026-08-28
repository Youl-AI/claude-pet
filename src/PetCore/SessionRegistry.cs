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
        if (!Directory.Exists(_directory))
            return Array.Empty<SessionRecord>();

        var records = new List<SessionRecord>();
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            try
            {
                using var stream = new FileStream(
                    file, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);

                var record = JsonSerializer.Deserialize<SessionRecord>(stream, Options);
                if (record is not null && !string.IsNullOrEmpty(record.SessionId))
                    records.Add(record);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // 훅이 쓰는 중일 수 있다. 건너뛴다.
            }
        }
        return records;
    }
}
