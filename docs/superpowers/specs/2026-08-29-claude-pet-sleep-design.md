# 클로드펫 낮잠(Sleeping) 상태 설계

토큰 한도(5시간 세션 / 주간 / 월 지출)에 도달하면 펫이 낮잠을 자고, 리셋되면 깨어난다.
"리셋까지 아무것도 진행되지 않는다"를 색과 포즈로 정직하게 보여주는 것이 목적이다.

## §1 감지 — 잠드는 신호

### 1.1 트랜스크립트의 한도 도달 줄 (실측 형식)

한도에 걸리면 세션 JSONL에 assistant 줄이 남는다. 실제 관측 (이 저장소 소유자의
트랜스크립트, 12건):

```json
{"type":"assistant", ...,
 "message":{..., "content":[{"type":"text","text":"You've hit your session limit · resets 6:10pm (Asia/Seoul)"}], "usage":{...0토큰...}},
 "error":"rate_limit",
 "isApiErrorMessage":true}
```

감지 키는 **줄 최상위의 `"error":"rate_limit"`** 이다. 문구가 아니라 이 enum 필드로
잡으므로, 아직 관측하지 못한 한도(주간)의 문구가 달라도 감지는 된다. 같은 자리에
`authentication_failed`, `server_error` 도 오는데(실측) 이들은 낮잠이 아니다 —
정확히 `rate_limit` 만.

실측된 문구 2종:
- `You've hit your session limit · resets 6:10pm (Asia/Seoul)` — 5시간 한도, 리셋 시각 있음
- `You've hit your monthly spend limit · raise it at claude.ai/...` — 월 지출 한도, 리셋 시각 없음

### 1.2 파서 인터페이스

`TranscriptEventKind` 에 `RateLimited` 를 추가하고, 이벤트에 리셋 예정 시각을 싣는다:

```csharp
public enum TranscriptEventKind { ToolUse, ToolResult, AssistantText, Thinking, Other, RateLimited }

public sealed record TranscriptEvent(
    TranscriptEventKind Kind,
    string? ToolName = null,
    bool IsError = false,
    long? ResetAtUnixMs = null);   // RateLimited 전용. 파싱 실패/시각 없음이면 null
```

판정 순서: 줄에 `"error":"rate_limit"` 가 있으면 다른 분류(AssistantText 등)보다
**먼저** `RateLimited` 로 분류한다. 이 줄은 assistant 형태라 기존 규칙대로면
"턴 종료(YourTurn)"로 오분류된다.

### 1.3 리셋 시각 파싱

content 텍스트에서 정규식으로 `resets {시각}` 을 뽑는다:

```
resets\s+(\d{1,2}):(\d{2})(am|pm)
```

- 표기 타임존(괄호 안 IANA 이름)은 사용자의 로컬 설정을 반영해 찍히므로,
  **로컬 타임존으로 해석**한다. 괄호 안 문자열은 사용하지 않는다.
- 해석한 시각이 현재보다 과거면 다음 날로 넘긴다 (밤 11시에 "resets 2:20am"을 보는 경우).
- 매칭 실패(월 지출 한도, 미지의 문구) → `ResetAtUnixMs = null`. 감지 자체는 유효하다.
- 파싱은 절대 던지지 않는다 — 실패는 null 이다.

## §2 상태 — 전역 플래그 (승인된 A안)

한도는 계정 전역이므로 세션별 상태머신이 아니라 **호스트가 하나의 플래그**를 든다.

- `PetState` 에 `Sleeping` 추가 (9번째 값).
- `PetHost` 가 보관: `private long? _sleepUntilUnixMs; private bool _sleeping;`
- 어느 세션에서든 `RateLimited` 이벤트가 오면 `_sleeping = true`,
  `_sleepUntilUnixMs = e.ResetAtUnixMs` (null 허용).
- 매 폴 틱, 기존 `PetStateMachine.Aggregate` 결과를 계산한 뒤 **잠들어 있으면
  `PetState.Sleeping` 으로 덮어쓴다.** 낮잠은 Blocked(빨강💢)·Abandoned(검정 누움)를
  포함한 모든 상태를 이긴다 — 한도가 걸리면 무엇을 해도 진행되지 않기 때문
  (사용자 결정).
- 세션별 상태머신은 건드리지 않는다. 한도 걸린 세션이 닫혀도 낮잠은 유지된다.

재시작 내구성: **없고, 의도적이다.** 펫은 세션에 부착할 때 `TranscriptTail.SkipToEnd()`
로 과거를 재생하지 않으므로, 낮잠 중 펫이 재시작하면 낮잠을 잊는다. 이를 복원하려고
상태를 저장하거나 부착 시 꼬리를 재검사하지 않는다 — 한도가 아직 걸려 있다면 다음
요청이 즉시 새 `rate_limit` 줄을 남겨 다시 잠들고, 리셋됐다면 잊는 것이 곧 정답이기
때문이다. 남는 손실은 "재시작 직후 ~첫 활동 전까지 낮잠 대신 일반 상태로 보이는
시간"뿐이며, 기능 오류가 아니라 시각적 아쉬움이다.

## §3 해제 — 깨어나는 신호 (셋 중 먼저 오는 것)

① **`quota_auto_resume_fired` 알림.** `plugin/hooks/hooks.json` 의 Notification
matcher 를 `permission_prompt|idle_prompt|quota_auto_resume_fired` 로 확장한다.
기존 notify 경로(훅 → `notify/*.json` → 1Hz drain)를 그대로 탄다. `PetHost` 가 이
타입을 받으면 즉시 기상. 세션별 상태머신의 `ApplyNotification` 으로는 **넘기지
않는다** — 이것은 전역 신호다.

② **리셋 시각 경과.** `_sleepUntilUnixMs` 가 있고 현재 시각이 지났으면 기상.
매 폴 틱에 검사한다.

③ **새로운 정상 활동 관측.** 잠든 이후 어느 세션에서든 `ToolUse` 또는 성공
`ToolResult` 이벤트가 관측되면 기상 — Claude가 실제로 다시 일하고 있다는 뜻이다.
시각 파싱이 실패한 한도(월 지출, 미지 문구)의 안전망. `AssistantText` 는 깨우지
않는다 (rate_limit 줄 자체가 assistant 형태라 오탐 여지를 남기지 않는다).

기상 시: `_sleeping = false`, `_sleepUntilUnixMs = null`, 다음 틱부터 원래 집계
상태로 복귀. 별도 기상 연출은 없다.

`quota_auto_resume_stale` / `quota_auto_resume_disabled` 는 이번 범위에서 다루지
않는다 — 어느 쪽이든 ②·③이 결국 상태를 정리한다 (YAGNI).

## §4 비주얼

- 스프라이트 시트에 **9번째 행**(인덱스 8) 추가: 256×256 → **256×288**.
  `tools/spritegen/spritegen.py` 의 `ROWS = 8 → 9`.
- 포즈: Abandoned(행 7)의 누운 몸 지오메트리 재사용.
- 몸 색: **흐린 청회색 `(96, 100, 122)`** — Abandoned 검정 `(52,50,60)` 과 구별되고,
  Error 보라와도 채도로 구별된다.
- 마크: 머리 위 픽셀 **Z**. 8프레임에 걸쳐 작은 Z가 떠오르며 커지고 큰 Z가
  사라지는 순환 — 정확한 프레임 배치는 구현 재량이되, 최소 2개의 Z 크기가
  등장하고 프레임 간 차이가 있어야 한다 (모든 프레임 동일 금지 — 이 저장소는
  동일 프레임 버그를 이미 한 번 겪었다).
- 눈: 감은 눈 (가로 1px 선). Abandoned 와 동일 방식이면 재사용.
- 레벨 명패(Lv 표기)는 낮잠 중에도 그대로 표시한다.
- `PetWindow` 의 상태→행 매핑에 `Sleeping → 8` 추가. 낮잠 중 이동은 없다
  (누워 있으므로) — Abandoned 와 동일한 정지 처리를 따른다.

## §5 훅 변경

`plugin/hooks/hooks.json` 한 곳:

```json
"matcher": "permission_prompt|idle_prompt|quota_auto_resume_fired"
```

`notification.ps1` 은 타입을 그대로 기록하므로 수정 불요.

## §6 테스트

- **파서**: `rate_limit` 줄 → `RateLimited` 분류 (실측 줄을 픽스처로), 리셋 시각
  파싱 (오늘/내일 넘김, am/pm, 실패 시 null), `authentication_failed`·`server_error`
  는 `RateLimited` 가 아님, 기존 assistant 분류로 오분류되지 않음.
- **호스트 로직**: 잠듦 → 집계 덮어씀, ①②③ 각각의 기상, 과거 리셋 시각의
  즉시 기상(유령 낮잠 방지). 호스트에서 분리 가능한 판정 로직은 `PetCore` 로
  내려 단위 테스트한다 (예: `SleepGate` 같은 순수 클래스).
- **비주얼**: `PetApp` 에는 테스트가 없다. 시트 치수(256×288)·행 8 프레임 상이함은
  spritegen 검증 스크립트로, 화면은 실행 캡처로 확인한다.

## §7 범위 밖

- `quota_auto_resume_stale` / `disabled` 의 별도 처리
- 리셋까지 남은 시간 표시 (명패나 마크에 카운트다운 없음)
- statusline 채널 (`rate_limits.*`) — 슬롯 점유가 필요해 기각
- 기상 연출 (링 이펙트 등)
