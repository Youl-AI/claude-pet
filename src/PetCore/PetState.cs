namespace PetCore;

public enum PetState
{
    Idle,
    Reading,
    Writing,
    Running,
    Error,

    // 아래 셋은 예전의 단일 NeedsYou 를 긴급도에 따라 쪼갠 것이다. 세 상황은
    // 사용자가 해야 할 일이 서로 다른데 하나의 빨강으로 뭉뚱그려져 있었다.
    YourTurn,   // 턴 종료 — 당신 차례. 산호주황 + 머리 위 물음표
    Blocked,    // permission_prompt — 클로드가 승인을 기다리며 멈춰 있다. 빨강 + 빠직
    Abandoned   // idle_prompt(60초) — 기다리다 지쳐 누웠다. 검게 가라앉아 납작
}
