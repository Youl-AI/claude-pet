namespace PetCore;

public sealed record SessionRecord(
    string SessionId,
    string TranscriptPath,
    int Pid,
    long PidStartUnixMs,
    long TouchedUnixMs);
