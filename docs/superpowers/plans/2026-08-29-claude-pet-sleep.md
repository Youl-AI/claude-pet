# 클로드펫 낮잠(Sleeping) 상태 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 토큰 한도 도달을 트랜스크립트에서 감지해 펫이 낮잠(청회색 누움 + Zzz)을 자고, 리셋 신호 셋 중 먼저 오는 것으로 깨어난다.

**Architecture:** `TranscriptParser`가 `"error":"rate_limit"` 줄을 `RateLimited` 이벤트로 분류하고 리셋 시각을 싣는다. `PetCore`의 순수 클래스 `SleepGate`가 잠듦/기상 판정을 전담하고, `PetHost`는 매 틱 `Aggregate` 결과를 `SleepGate`가 잠들어 있으면 `PetState.Sleeping`으로 덮어쓴다. 스프라이트 시트에 9번째 행이 추가된다.

**Tech Stack:** .NET 10 (`PetCore` 플랫폼 중립 + `PetApp` WPF), xUnit, 무의존 Python PNG 생성기(`tools/spritegen`).

**스펙:** `docs/superpowers/specs/2026-08-29-claude-pet-sleep-design.md`

## Global Constraints

- **"절대 던지지 않는다"는 계약은 구조로 보장한다. 예외 종류를 열거하지 않는다.** 이 저장소에서 `catch (IOException)`만 잡았다가 `UnauthorizedAccessException`에 세 번 뚫린 이력이 있다. 파일·프로세스·리플렉션을 건드리는 공개 메서드는 메서드 경계 전체를 `catch (Exception)`으로 감싼다. 좁은 catch는 리뷰에서 반려된 전례가 있다.
- **렌더 스레드를 막지 않는다.** 12fps 루프 안에서 파일을 읽지 않는다.
- **낮잠은 모든 상태를 이긴다** (Blocked·Abandoned 포함, 스펙 §2 — 사용자 결정).
- 감지 키는 줄 최상위 `"error":"rate_limit"` **정확히 이 값만**. `authentication_failed`, `server_error`는 낮잠이 아니다 (스펙 §1.1).
- 리셋 시각은 **로컬 타임존**으로 해석하고, 과거면 다음 날로 넘긴다. 파싱 실패는 null이지 예외가 아니다 (스펙 §1.3).
- `AssistantText`는 기상 신호가 아니다. 기상시키는 활동은 `ToolUse`와 성공 `ToolResult`뿐 (스펙 §3-③).
- 낮잠 상태의 스프라이트 행은 8프레임이 전부 서로 달라야 한다 (스펙 §4 — 동일 프레임 버그 전례).
- `PetCore`는 `net10.0`이고 Windows 전용 API를 참조하지 않는다.
- 커밋 메시지는 영어로 쓴다 (`feat:`/`fix:`/`docs:` 소문자, 끝에 `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`). 새 파일 주석은 한국어로 쓴다.
- **`plugin/bin/` 을 커밋하지 않는다.** 검증 빌드는 `/tmp/pet-verify`. 배포 바이너리는 전 태스크 완료 후 컨트롤러가 한 번만 재빌드한다.

**빌드/테스트 명령** (저장소 루트에서):

```bash
dotnet test tests/PetCore.Tests            # 베이스라인 155개 통과
dotnet build src/PetApp/PetApp.csproj -o /tmp/pet-verify
```

---

### Task 1: RateLimited 이벤트 — 파서와 리셋 시각

**Files:**
- Modify: `src/PetCore/TranscriptEvent.cs`
- Create: `src/PetCore/RateLimitReset.cs`
- Modify: `src/PetCore/TranscriptParser.cs`
- Test: `tests/PetCore.Tests/RateLimitEventTests.cs`

**Interfaces:**
- Consumes: 기존 `TranscriptParser.ParseLine(string)`, `TranscriptEvent`
- Produces:
  - `TranscriptEventKind.RateLimited` (enum 값 추가, `Other` 뒤)
  - `TranscriptEvent`에 `long? ResetAtUnixMs = null` positional 파라미터 추가
  - `public static long? RateLimitReset.Resolve(string? text, DateTimeOffset now)` — 순수 함수

- [ ] **Step 1: Write the failing test**

`tests/PetCore.Tests/RateLimitEventTests.cs`:

```csharp
using PetCore;

public class RateLimitEventTests
{
    // 실측 형식 (스펙 §1.1). usage 토큰은 전부 0으로 온다.
    private const string SessionLimitLine =
        """{"type":"assistant","uuid":"x","message":{"id":"msg_rl1","role":"assistant","model":"claude-opus-5","content":[{"type":"text","text":"You've hit your session limit · resets 6:10pm (Asia/Seoul)"}],"usage":{"input_tokens":0,"output_tokens":0}},"error":"rate_limit","isApiErrorMessage":true}""";

    private const string MonthlyLimitLine =
        """{"type":"assistant","uuid":"x","message":{"id":"msg_rl2","role":"assistant","model":"claude-opus-5","content":[{"type":"text","text":"You've hit your monthly spend limit · raise it at claude.ai/settings"}],"usage":{"input_tokens":0,"output_tokens":0}},"error":"rate_limit","isApiErrorMessage":true}""";

    private const string AuthErrorLine =
        """{"type":"assistant","uuid":"x","message":{"id":"msg_ae","role":"assistant","content":[{"type":"text","text":"OAuth access token has expired."}]},"error":"authentication_failed","isApiErrorMessage":true}""";

    [Fact]
    public void RateLimitLineBecomesOneRateLimitedEvent()
    {
        var events = TranscriptParser.ParseLine(SessionLimitLine);

        var e = Assert.Single(events);
        Assert.Equal(TranscriptEventKind.RateLimited, e.Kind);
    }

    [Fact]
    public void RateLimitLineIsNotMisreadAsTurnEnd()
    {
        // 이 줄은 assistant text 형태라 기존 규칙대로면 AssistantText(턴 종료)가 된다.
        var events = TranscriptParser.ParseLine(SessionLimitLine);
        Assert.DoesNotContain(events, e => e.Kind == TranscriptEventKind.AssistantText);
    }

    [Fact]
    public void SessionLimitCarriesTheResetInstant()
    {
        var e = Assert.Single(TranscriptParser.ParseLine(SessionLimitLine));
        Assert.NotNull(e.ResetAtUnixMs);

        // ParseLine 내부의 '지금'은 주입할 수 없으므로 정확한 값 대신 범위로 확인한다:
        // 리셋은 지금 이후, 24시간 이내여야 한다 (과거면 다음 날로 넘긴다는 규칙의 귀결).
        var now = DateTimeOffset.Now;
        Assert.InRange(e.ResetAtUnixMs.Value,
            now.ToUnixTimeMilliseconds(),
            now.AddHours(24).ToUnixTimeMilliseconds());
    }

    [Fact]
    public void MonthlyLimitHasNoResetInstantButStillSleeps()
    {
        var e = Assert.Single(TranscriptParser.ParseLine(MonthlyLimitLine));
        Assert.Equal(TranscriptEventKind.RateLimited, e.Kind);
        Assert.Null(e.ResetAtUnixMs);
    }

    [Fact]
    public void OtherApiErrorsAreNotRateLimited()
    {
        var events = TranscriptParser.ParseLine(AuthErrorLine);
        Assert.DoesNotContain(events, e => e.Kind == TranscriptEventKind.RateLimited);
    }

    // --- RateLimitReset.Resolve: 시계를 주입하는 순수 함수 ---

    private static readonly DateTimeOffset Noon =
        new(2026, 8, 29, 12, 0, 0, TimeSpan.FromHours(9));

    [Fact]
    public void ResolvesAFutureTimeToday()
    {
        var ms = RateLimitReset.Resolve("You've hit your session limit · resets 6:10pm (Asia/Seoul)", Noon);
        Assert.Equal(new DateTimeOffset(2026, 8, 29, 18, 10, 0, TimeSpan.FromHours(9)).ToUnixTimeMilliseconds(), ms);
    }

    [Fact]
    public void RollsAPastTimeToTomorrow()
    {
        // 정오에 "resets 2:20am"을 보면 내일 새벽이다.
        var ms = RateLimitReset.Resolve("You've hit your session limit · resets 2:20am (Asia/Seoul)", Noon);
        Assert.Equal(new DateTimeOffset(2026, 8, 30, 2, 20, 0, TimeSpan.FromHours(9)).ToUnixTimeMilliseconds(), ms);
    }

    [Fact]
    public void TwelveOclockEdgesResolveCorrectly()
    {
        // 12am = 0시, 12pm = 12시. 12시간제 파싱의 고전적 함정.
        var am = RateLimitReset.Resolve("resets 12:05am (Asia/Seoul)", Noon);
        Assert.Equal(new DateTimeOffset(2026, 8, 30, 0, 5, 0, TimeSpan.FromHours(9)).ToUnixTimeMilliseconds(), am);

        var pm = RateLimitReset.Resolve("resets 12:30pm (Asia/Seoul)", Noon);
        Assert.Equal(new DateTimeOffset(2026, 8, 29, 12, 30, 0, TimeSpan.FromHours(9)).ToUnixTimeMilliseconds(), pm);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("You've hit your monthly spend limit · raise it at claude.ai")]
    [InlineData("resets soon")]
    [InlineData("resets 25:99xx")]
    public void UnparseableTextYieldsNull(string? text)
    {
        Assert.Null(RateLimitReset.Resolve(text, Noon));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PetCore.Tests --filter RateLimitEventTests`
Expected: 컴파일 실패 — `RateLimited`, `ResetAtUnixMs`, `RateLimitReset` 미정의.

- [ ] **Step 3: Implement**

`src/PetCore/TranscriptEvent.cs` — enum 값과 필드 추가:

```csharp
public enum TranscriptEventKind
{
    ToolUse,
    ToolResult,
    AssistantText,
    Thinking,
    Other,
    RateLimited
}

public sealed record TranscriptEvent(
    TranscriptEventKind Kind,
    string? ToolName = null,
    bool IsError = false,
    long? ResetAtUnixMs = null);
```

`src/PetCore/RateLimitReset.cs` (새 파일):

```csharp
using System.Globalization;
using System.Text.RegularExpressions;

namespace PetCore;

/// <summary>
/// 한도 도달 문구에서 리셋 예정 시각을 뽑는다.
///
/// 실측 문구: "You've hit your session limit · resets 6:10pm (Asia/Seoul)".
/// 괄호 안 타임존은 사용자의 로컬 설정을 반영해 찍히므로 따로 해석하지 않고
/// now(호출자의 로컬 오프셋)를 그대로 쓴다. 해석한 시각이 과거면 다음 날로
/// 넘긴다 — 밤 11시에 "resets 2:20am"을 보는 경우다. (스펙 §1.3)
/// </summary>
public static partial class RateLimitReset
{
    [GeneratedRegex(@"resets\s+(\d{1,2}):(\d{2})\s*(am|pm)", RegexOptions.IgnoreCase)]
    private static partial Regex ResetsPattern();

    /// <summary>파싱 실패는 null이다. 절대 던지지 않는다.</summary>
    public static long? Resolve(string? text, DateTimeOffset now)
    {
        try
        {
            if (string.IsNullOrEmpty(text)) return null;

            var m = ResetsPattern().Match(text);
            if (!m.Success) return null;

            var hour = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var minute = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            if (hour is < 1 or > 12 || minute > 59) return null;

            var pm = m.Groups[3].Value.Equals("pm", StringComparison.OrdinalIgnoreCase);
            var hour24 = (hour % 12) + (pm ? 12 : 0);   // 12am -> 0시, 12pm -> 12시

            var reset = new DateTimeOffset(
                now.Year, now.Month, now.Day, hour24, minute, 0, now.Offset);
            if (reset <= now) reset = reset.AddDays(1);

            return reset.ToUnixTimeMilliseconds();
        }
        catch (Exception)
        {
            return null;   // 문구는 신뢰할 수 없는 입력이다. 실패는 "시각 모름"이다.
        }
    }
}
```

`src/PetCore/TranscriptParser.cs` — `message` 확인 **앞**에 분기를 넣는다.
`root`를 얻은 직후(`ValueKind != Object` 검사 다음)에 추가:

```csharp
            // 한도 도달 줄은 assistant text 형태라 아래 규칙대로면 "턴 종료"로
            // 오분류된다. 최상위 error 필드를 먼저 본다 — 정확히 "rate_limit"만.
            // authentication_failed / server_error 도 같은 자리에 오지만(실측)
            // 그것들은 낮잠이 아니다. (스펙 §1.1)
            if (root.TryGetProperty("error", out var errorProp)
                && errorProp.ValueKind == JsonValueKind.String
                && errorProp.GetString() == "rate_limit")
            {
                return new[]
                {
                    new TranscriptEvent(
                        TranscriptEventKind.RateLimited,
                        ResetAtUnixMs: RateLimitReset.Resolve(
                            FirstTextContent(root), DateTimeOffset.Now)),
                };
            }
```

같은 파일에 private 헬퍼 추가:

```csharp
    /// <summary>message.content 배열에서 첫 text 블록의 문자열. 없으면 null.</summary>
    private static string? FirstTextContent(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.Object) return null;
        if (!message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array) return null;

        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("type", out var t)
                && t.ValueKind == JsonValueKind.String
                && t.GetString() == "text"
                && item.TryGetProperty("text", out var txt)
                && txt.ValueKind == JsonValueKind.String)
                return txt.GetString();
        }
        return null;
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PetCore.Tests`
Expected: 전체 통과 (155 + 새 테스트). 기존 `TranscriptParserTests` 회귀 없음.

- [ ] **Step 5: Commit**

```bash
git add src/PetCore/TranscriptEvent.cs src/PetCore/RateLimitReset.cs src/PetCore/TranscriptParser.cs tests/PetCore.Tests/RateLimitEventTests.cs
git commit -m "feat: classify usage-limit transcript lines as RateLimited events"
```

---

### Task 2: SleepGate — 잠듦/기상 판정

**Files:**
- Create: `src/PetCore/SleepGate.cs`
- Test: `tests/PetCore.Tests/SleepGateTests.cs`

**Interfaces:**
- Consumes: `TranscriptEvent`, `TranscriptEventKind` (Task 1)
- Produces:

```csharp
public sealed class SleepGate
{
    public bool Sleeping { get; }
    public void Observe(TranscriptEvent e, long nowUnixMs);  // RateLimited -> 잠듦, ToolUse/성공 ToolResult -> 기상(③)
    public void OnQuotaResumed();                            // 훅 ① -> 기상
    public bool IsSleeping(long nowUnixMs);                  // ② 리셋 시각 경과 검사 후 현재 상태
}
```

- [ ] **Step 1: Write the failing test**

`tests/PetCore.Tests/SleepGateTests.cs`:

```csharp
using PetCore;

public class SleepGateTests
{
    private const long T0 = 1_000_000_000_000;
    private static TranscriptEvent RateLimited(long? resetAt) =>
        new(TranscriptEventKind.RateLimited, ResetAtUnixMs: resetAt);

    [Fact]
    public void StartsAwake()
    {
        Assert.False(new SleepGate().IsSleeping(T0));
    }

    [Fact]
    public void RateLimitedPutsItToSleep()
    {
        var gate = new SleepGate();
        gate.Observe(RateLimited(T0 + 60_000), T0);
        Assert.True(gate.IsSleeping(T0));
    }

    [Fact]
    public void WakesWhenTheResetInstantPasses()
    {
        var gate = new SleepGate();
        gate.Observe(RateLimited(T0 + 60_000), T0);

        Assert.True(gate.IsSleeping(T0 + 59_999));
        Assert.False(gate.IsSleeping(T0 + 60_000));   // 경계 포함: 시각이 되면 깬다
    }

    [Fact]
    public void AStaleRateLimitLineDoesNotCauseAGhostNap()
    {
        // 리셋 시각이 이미 지난 과거 줄 — 잠들자마자 다음 검사에서 깬다 (스펙 §2).
        var gate = new SleepGate();
        gate.Observe(RateLimited(T0 - 1), T0);
        Assert.False(gate.IsSleeping(T0));
    }

    [Fact]
    public void QuotaResumedWakesImmediately()
    {
        var gate = new SleepGate();
        gate.Observe(RateLimited(T0 + 3_600_000), T0);
        gate.OnQuotaResumed();
        Assert.False(gate.IsSleeping(T0));
    }

    [Fact]
    public void FreshToolActivityWakes()
    {
        var gate = new SleepGate();
        gate.Observe(RateLimited(null), T0);          // 시각 모름 (월 지출 한도)
        Assert.True(gate.IsSleeping(T0));

        gate.Observe(new TranscriptEvent(TranscriptEventKind.ToolUse, "Read"), T0 + 1_000);
        Assert.False(gate.IsSleeping(T0 + 1_000));
    }

    [Fact]
    public void SuccessfulToolResultWakes()
    {
        var gate = new SleepGate();
        gate.Observe(RateLimited(null), T0);
        gate.Observe(new TranscriptEvent(TranscriptEventKind.ToolResult, IsError: false), T0 + 1_000);
        Assert.False(gate.IsSleeping(T0 + 1_000));
    }

    [Fact]
    public void AssistantTextAndErrorsDoNotWake()
    {
        var gate = new SleepGate();
        gate.Observe(RateLimited(null), T0);

        gate.Observe(new TranscriptEvent(TranscriptEventKind.AssistantText), T0 + 1_000);
        gate.Observe(new TranscriptEvent(TranscriptEventKind.ToolResult, IsError: true), T0 + 1_000);
        gate.Observe(new TranscriptEvent(TranscriptEventKind.Thinking), T0 + 1_000);
        Assert.True(gate.IsSleeping(T0 + 1_000));
    }

    [Fact]
    public void ANewRateLimitWhileAsleepExtendsTheNap()
    {
        var gate = new SleepGate();
        gate.Observe(RateLimited(T0 + 60_000), T0);
        gate.Observe(RateLimited(T0 + 120_000), T0 + 30_000);   // 더 늦은 리셋으로 갱신
        Assert.True(gate.IsSleeping(T0 + 90_000));
        Assert.False(gate.IsSleeping(T0 + 120_000));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PetCore.Tests --filter SleepGateTests`
Expected: 컴파일 실패 — `SleepGate` 미정의.

- [ ] **Step 3: Implement**

`src/PetCore/SleepGate.cs`:

```csharp
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
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PetCore.Tests`
Expected: 전체 통과.

- [ ] **Step 5: Commit**

```bash
git add src/PetCore/SleepGate.cs tests/PetCore.Tests/SleepGateTests.cs
git commit -m "feat: gate the sleeping state on rate-limit and wake signals"
```

---

### Task 3: Sleeping 스프라이트 행

**Files:**
- Modify: `src/PetCore/PetState.cs` — `Sleeping` 추가
- Modify: `tools/spritegen/spritegen.py` — 9번째 행
- Modify: `src/PetApp/SpriteSheet.cs:17` — `Rows = 9`
- Modify: `src/PetApp/assets/pet.png` — 재생성 (256×288)

`PetApp`에는 테스트 프로젝트가 없다. 검증은 스크립트 실행 결과와 빌드로 한다.

**Interfaces:**
- Consumes: 기존 `draw_pet_lying`, `LY_*` 지오메트리, `ROW_*` 테이블 (spritegen.py)
- Produces: `PetState.Sleeping` (enum 9번째 값), pet.png 행 8 (인덱스), 256×288 시트

- [ ] **Step 1: PetState에 Sleeping 추가**

`src/PetCore/PetState.cs`:

```csharp
public enum PetState { Idle, Reading, Writing, Running, Error, YourTurn, Blocked, Abandoned, Sleeping }
```

`src/PetApp/SpriteSheet.cs:17`의 `private const int Rows = 8;`을 `= 9;`로 바꾸고
그 줄 주석의 `256x256 / 32 = 8x8`도 `256x288`에 맞게 고친다. (이 클래스는 생성자에서
Rows와 `Enum.GetValues<PetState>().Length`가 다르면 던지므로, enum과 상수는 한
커밋에서 같이 바뀌어야 한다.)

- [ ] **Step 2: spritegen.py에 9번째 행 추가**

`tools/spritegen/spritegen.py` 수정 네 곳:

(1) `ROWS = 8` → `ROWS = 9`

(2) `ROW_BODY_COLORS`에 추가 (Abandoned 다음):

```python
    (96, 100, 122),   # 8 Sleeping  — 흐린 청회색. 검정(Abandoned)과도, Error 회청과도
                      #   구별된다. 리셋까지 강제 휴식이라는 "꺼짐"이 아니라 "잠듦".
```

`ROW_EYE_COLORS`(spritegen.py:80)에도 같은 위치에 `ABANDONED_EYE_COLOR,  # 8 Sleeping`
을 추가한다 — 누운 포즈의 감은 눈 선과 같은 처리를 재사용하는 것이다.

(3) Z 마크 드로잉과 프레임 함수 (`abandoned_frame` 뒤에 추가):

```python
# 8 Sleeping — 토큰 한도. 리셋까지 강제 휴식. Abandoned와 같은 누운 몸이지만
# 색이 청회색이고 머리 위로 Z가 떠오른다. 8프레임 순환: 작은 Z가 몸 가까이서
# 나타나 위로 떠오르며 커지고, 큰 Z가 옅어지듯 사라진다. 프레임마다 Z의
# 위치/조합이 달라 어느 두 프레임도 같지 않다.
Z_SMALL = (
    "111",
    "010",
    "111",
)
Z_BIG = (
    "11111",
    "00010",
    "00100",
    "01000",
    "11111",
)

# (dx, dy, glyph) — 몸 오른쪽 위(코 근처 x=22, 몸 윗변 y=20 기준)에서의 상대 위치.
# 위로 갈수록(작은 dy) 나중 단계다.
SLEEP_Z_FRAMES = (
    ((22, 14, Z_SMALL),),
    ((21, 12, Z_SMALL),),
    ((20, 10, Z_SMALL), (25, 15, Z_SMALL)),
    ((19, 7, Z_BIG), (24, 13, Z_SMALL)),
    ((18, 5, Z_BIG), (23, 11, Z_SMALL)),
    ((17, 3, Z_BIG), (22, 9, Z_SMALL)),
    ((16, 1, Z_BIG),),
    ((22, 15, Z_SMALL), (17, 2, Z_BIG)),
)
assert len(SLEEP_Z_FRAMES) == COLS
assert len(set(SLEEP_Z_FRAMES)) == COLS, "낮잠 Z 프레임이 전부 서로 달라야 함"

Z_COLOR = (222, 226, 240)  # 몸보다 밝은 청백색 — 어두운 배경에서도 뜬다.


def draw_z_glyph(px, ox, oy, gx, gy, glyph):
    for row_i, row in enumerate(glyph):
        for col_i, ch in enumerate(row):
            if ch == "1":
                put(px, ox, oy, gx + col_i, gy + row_i, Z_COLOR + (255,))


def sleeping_frame(col):
    return dict(lying=True, squash=ABANDONED_SQUASH[col], zs=SLEEP_Z_FRAMES[col])
```

(참고: `put`(spritegen.py:106)은 클리핑하지 **않는다** — 셀(0..31)을 벗어나는 쓰기는
assert로 즉사한다. 위 `SLEEP_Z_FRAMES` 좌표는 전수 검산 완료: Z_BIG(5×5)의 최소
dy=1 → y 1..5, 최대 x는 25+4=29 < 32. Z_SMALL(3×3)의 최대는 (25,15) → x 25..27,
y 15..17. 전부 셀 안이고 몸 윗변(y=20) 위 공간이라 squash 와도 겹치지 않는다.
좌표를 바꾸려면 이 assert가 그대로 안전망이 된다.)

(4) `ROW_FRAME_FNS`에 `sleeping_frame,  # 8 Sleeping` 추가. 그리고 `main()`의
프레임 렌더 루프(spritegen.py:430 근처)를 고친다. 이 루프는 `params.pop(...)`으로
오버레이 키를 꺼낸 뒤 **나머지 `params` 전부를 `draw_pet_lying(..., **params)`로
넘기므로**, `zs`를 pop 하지 않으면 `draw_pet_lying()이 예상 못 한 키워드 인자
zs`로 TypeError가 난다. 기존 pop 줄들 옆에 추가:

```python
            zs = params.pop("zs", ())
```

그리고 `question_dy` 처리 뒤에 그리기를 추가:

```python
            for gx, gy, glyph in zs:
                draw_z_glyph(px, ox, oy, gx, gy, glyph)
```

- [ ] **Step 3: 시트 재생성과 검증**

```bash
python tools/spritegen/spritegen.py
python - <<'EOF'
import struct
d = open('src/PetApp/assets/pet.png','rb').read()
w, h = int.from_bytes(d[16:20],'big'), int.from_bytes(d[20:24],'big')
assert (w, h) == (256, 288), (w, h)
print('sheet', w, 'x', h, '- OK')
EOF
```

Expected: `sheet 256 x 288 - OK`. 그리고 행 8의 8프레임이 pairwise 서로 다른지
검사한다 (이 저장소의 flashgen 검증과 같은 방식 — PNG를 디코드해 프레임별 바이트를
비교. Task 8 리포트 `.superpowers/sdd/task-8-report.md`에 선례가 있다).

- [ ] **Step 4: 빌드 확인**

```bash
dotnet build src/PetApp/PetApp.csproj -o /tmp/pet-verify   # 경고 0
dotnet test tests/PetCore.Tests                            # PetState 추가로 인한 회귀 없음
```

- [ ] **Step 5: Commit**

```bash
git add src/PetCore/PetState.cs src/PetApp/SpriteSheet.cs tools/spritegen/spritegen.py src/PetApp/assets/pet.png
git commit -m "feat: draw the sleeping row - lying blue-gray body with rising Zs"
```

---

### Task 4: 호스트 배선 + 훅 — 낮잠이 화면에 닿는다

**Files:**
- Modify: `src/PetApp/PetHost.cs`
- Modify: `src/PetApp/PetWindow.xaml.cs:131` (resting 판정)
- Modify: `plugin/hooks/hooks.json` (Notification matcher)

`PetApp`에는 테스트 프로젝트가 없다. 판정 로직은 Task 1·2에서 이미 단위 테스트됐고,
이 태스크는 배선이다. 검증은 빌드 + 컨트롤러의 실행 확인.

**Interfaces:**
- Consumes: `SleepGate` (Task 2), `PetState.Sleeping` (Task 3), 기존 `PollCore`/`DrainNotifications` 구조

- [ ] **Step 1: PetHost에 SleepGate 통합**

`src/PetApp/PetHost.cs` 수정 네 곳:

(1) 필드 추가 (`_usage` 근처):

```csharp
    // 토큰 한도 낮잠. 한도는 계정 전역이므로 세션별 머신이 아니라 여기 하나다 (스펙 §2).
    private readonly SleepGate _sleep = new();
```

(2) `PollCore()`의 트랜스크립트 루프 — 기존:

```csharp
            var machine = _machines[session.SessionId];
            foreach (var e in tail.ReadNew())
                machine.Apply(e);
```

를 다음으로 바꾼다:

```csharp
            var machine = _machines[session.SessionId];
            var nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var e in tail.ReadNew())
            {
                machine.Apply(e);      // RateLimited 는 머신의 switch 에 케이스가 없어
                                       // 상태를 바꾸지 않는다 — Sequence 만 오른다.
                _sleep.Observe(e, nowUnixMs);
            }
```

(3) `PollCore()` 끝의 상태 반영 — 기존:

```csharp
        DrainNotifications();
        _window.SetState(PetStateMachine.Aggregate(_machines.Values));
```

를 다음으로 바꾼다:

```csharp
        DrainNotifications();

        // 낮잠은 모든 상태를 이긴다 (스펙 §2, 사용자 결정) — 한도가 걸리면
        // 권한을 승인해도 리셋 전에는 아무것도 진행되지 않기 때문이다.
        var aggregate = PetStateMachine.Aggregate(_machines.Values);
        var state = _sleep.IsSleeping(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            ? PetState.Sleeping
            : aggregate;
        _window.SetState(state);
```

(4) `DrainNotifications()`의 알림 적용 자리 — 기존 `machine.ApplyNotification(...)`
블록을 다음으로 바꾼다 (전역 신호를 세션 머신보다 먼저 가로챈다):

```csharp
                    if (root.TryGetProperty("notificationType", out var type))
                    {
                        var typeName = type.GetString() ?? "";

                        if (typeName == "quota_auto_resume_fired")
                        {
                            // 전역 신호 — 세션 머신으로 보내지 않는다 (스펙 §3-①).
                            _sleep.OnQuotaResumed();
                        }
                        else if (root.TryGetProperty("sessionId", out var sid)
                                 && sid.GetString() is { } sessionId
                                 && _machines.TryGetValue(sessionId, out var machine))
                        {
                            machine.ApplyNotification(typeName);
                        }
                    }
```

- [ ] **Step 2: PetWindow의 정지 판정에 Sleeping 포함**

`src/PetApp/PetWindow.xaml.cs:131` — 기존:

```csharp
        var resting = _state == PetState.Idle || _state == PetState.Abandoned;
```

를 다음으로 바꾼다:

```csharp
        var resting = _state is PetState.Idle or PetState.Abandoned or PetState.Sleeping;
```

`Tick()`의 이동 switch에는 `Sleeping` 케이스를 추가하지 **않는다** — 케이스가 없는
상태는 이동하지 않고(Abandoned와 동일), 그것이 원하는 동작이다 (스펙 §4).

- [ ] **Step 3: 훅 matcher 확장**

`plugin/hooks/hooks.json`:

```json
"matcher": "permission_prompt|idle_prompt|quota_auto_resume_fired"
```

`notification.ps1`은 타입을 그대로 기록하므로 수정하지 않는다 (스펙 §5).

- [ ] **Step 4: 빌드와 전체 테스트**

```bash
dotnet build src/PetApp/PetApp.csproj -o /tmp/pet-verify   # 경고 0
dotnet test tests/PetCore.Tests                            # 전체 통과
```

- [ ] **Step 5: Commit**

```bash
git add src/PetApp/PetHost.cs src/PetApp/PetWindow.xaml.cs plugin/hooks/hooks.json
git commit -m "feat: put the pet to sleep when usage limits are hit"
```

---

## 계획 밖 (전 태스크 완료 후 컨트롤러가 직접)

1. 실행 검증: 가짜 세션 + `rate_limit` 줄을 넣은 트랜스크립트로 낮잠 진입·Z 애니메이션·기상 3경로를 화면 캡처로 확인.
2. `plugin/bin/pet.exe` 재빌드 + 커밋, `plugin.json` 버전 0.4.0 → 0.5.0.
3. 푸시.
