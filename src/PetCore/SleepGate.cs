namespace PetCore;

/// <summary>
/// 토큰 한도 낮잠의 잠듦/기상 판정 (스펙 §2·§3). 한도는 계정 전역이므로
/// 세션별 상태머신이 아니라 호스트가 이 게이트 하나를 든다.
///
/// 기상은 셋 중 먼저 오는 것: ① quota_auto_resume_fired 알림(OnQuotaResumed),
/// ② 리셋 시각 경과(IsSleeping의 검사), ③ 새로운 도구 활동 관측(Observe).
/// AssistantText 는 깨우지 않는다 — 한도 도달 줄 자체가 assistant 형태라
/// 오탐 여지를 남기지 않기 위해서다.
/// </summary>
public sealed class SleepGate
{
    private long? _resetAtUnixMs;

    public bool Sleeping { get; private set; }

    /// <summary>트랜스크립트 이벤트를 관찰한다. 어느 세션의 것이든 상관없다.</summary>
    public void Observe(TranscriptEvent e, long nowUnixMs)
    {
        switch (e.Kind)
        {
            case TranscriptEventKind.RateLimited:
                Sleeping = true;
                _resetAtUnixMs = e.ResetAtUnixMs;
                break;

            case TranscriptEventKind.ToolUse:
            case TranscriptEventKind.ToolResult when !e.IsError:
                // Claude 가 실제로 다시 일하고 있다 — 시각 파싱이 실패한 한도의 안전망.
                Wake();
                break;
        }
    }

    public void OnQuotaResumed() => Wake();

    /// <summary>리셋 시각이 지났으면 여기서 깨운 뒤, 현재 잠듦 여부를 돌려준다.</summary>
    public bool IsSleeping(long nowUnixMs)
    {
        if (Sleeping && _resetAtUnixMs is { } resetAt && nowUnixMs >= resetAt)
            Wake();
        return Sleeping;
    }

    private void Wake()
    {
        Sleeping = false;
        _resetAtUnixMs = null;
    }
}
