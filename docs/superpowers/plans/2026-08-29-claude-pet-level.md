# claude-pet 레벨 시스템 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 펫이 사용자의 Claude 사용량(API 환산 비용)에 따라 레벨을 올리고, 그 레벨을 펫 왼쪽 명패에 숫자로 표시한다.

**Architecture:** 계산은 전부 `PetCore`(플랫폼 중립, 유닛 테스트 가능)에 넣고, `PetApp`은 표시만 한다. 데이터는 펫이 이미 읽고 있는 `~/.claude/projects/**/*.jsonl`에서 나오므로 Claude Code 쪽에 새 훅도 새 I/O도 만들지 않는다. 전량 스캔은 최초 1회뿐이고, 이후에는 상태 파일에 저장된 파일별 (크기, mtime, 비용)을 보고 **바뀐 파일만** 다시 읽는다.

**Tech Stack:** .NET 10 (`net10.0` / `net10.0-windows`), WPF, xUnit, `System.Text.Json`. 스프라이트 생성은 Python 3 표준 라이브러리만 사용(외부 의존성 없음).

## Global Constraints

이 절의 규칙은 **모든 태스크의 요구사항에 암묵적으로 포함된다.**

- **"절대 던지지 않는다"는 계약은 구조로 보장한다. 예외 종류를 열거하지 않는다.** 이 저장소에서 `catch (IOException)`만 잡았다가 `UnauthorizedAccessException`에 세 번 뚫린 이력이 있다. 파일·프로세스·리플렉션을 건드리는 공개 메서드는 메서드 경계 전체를 `catch (Exception)`으로 감싼다.
- **렌더 스레드를 막지 않는다.** 12fps 루프 안에서 파일을 읽지 않는다.
- **금액을 화면에 노출하지 않는다.** 명패에는 레벨 숫자만 표시한다 (스펙 §2.4).
- **돈은 `decimal`로 다룬다.** `double`은 곡선 계산에만 쓴다.
- **정본 상수는 `A`, `C0`, `C2`, `M3` 넷뿐이다.** `M4`, `C3`, `C4`는 계산해서 얻는다. 하드코딩 금지 (스펙 §4.1).
- **레벨 표시는 1 미만이면 1로, 9999 초과면 9999로 클램프한다.**
- `PetCore`는 `net10.0`이고 Windows 전용 API를 참조하지 않는다. 기존 파일들이 그렇게 되어 있다.
- 커밋 메시지는 영어로 쓴다. 기존 이력과 맞춘다.
- 새 파일의 주석은 한국어로 쓴다. 기존 파일들이 그렇게 되어 있다.
- **`plugin/bin/` 을 커밋하지 않는다.** 그 안의 `pet.exe` 는 git 추적 대상이지만 배포 바이너리이고, 모든 태스크가 끝난 뒤 한 번만 다시 만든다. 검증용 빌드는 `/tmp/pet-verify` 로 낸다.

**정본 상수 (스펙 §4.1)**

```
A  = 13.6869      C0 = 3.61       C2 = 4992.0      M3 = 1396.17
M4 = M3 × 1.9  = 2652.723
C3 = C2 + 900  × M3 = 1,261,545
C4 = C3 + 8999 × M4 = 25,133,399.277
```

---

## File Structure

**새로 만드는 파일**

| 경로 | 책임 |
|---|---|
| `src/PetCore/UsagePricing.cs` | 모델 단가표, 토큰 4종 → 달러 |
| `src/PetCore/UsageLineParser.cs` | 트랜스크립트 한 줄 → `UsageRecord` |
| `src/PetCore/LevelCurve.cs` | 비용 → 레벨 (3구간 수식) |
| `src/PetCore/TranscriptCostScanner.cs` | 파일 하나 스캔, `message.id` 중복 제거, 비용 |
| `src/PetCore/UsageState.cs` | 상태 파일의 자료형 |
| `src/PetCore/UsageStore.cs` | 상태 파일 읽기/쓰기 |
| `src/PetCore/UsageTracker.cs` | 조립: 파일 목록 → 캐시 판단 → 총비용 → 레벨 |
| `src/PetApp/PlateRenderer.cs` | 명패 비트맵 생성 (3×5 픽셀 숫자) |
| `src/PetApp/LevelFlash.cs` | 레벨업 이펙트 프레임 재생 상태 |
| `tools/spritegen/flashgen.py` | `flash.png` 생성 |
| `src/PetApp/assets/flash.png` | 8프레임 × 32×32 (생성물, 커밋함) |
| `tests/PetCore.Tests/UsagePricingTests.cs` | |
| `tests/PetCore.Tests/UsageLineParserTests.cs` | |
| `tests/PetCore.Tests/LevelCurveTests.cs` | |
| `tests/PetCore.Tests/TranscriptCostScannerTests.cs` | |
| `tests/PetCore.Tests/UsageStoreTests.cs` | |
| `tests/PetCore.Tests/UsageTrackerTests.cs` | |

**고치는 파일**

| 경로 | 무엇을 |
|---|---|
| `src/PetApp/PetApp.csproj` | `flash.png` 리소스 등록 |
| `src/PetApp/PetWindow.xaml` | 폭 64→112, Canvas로 바꾸고 Plate/Flash 이미지 추가 |
| `src/PetApp/PetWindow.xaml.cs` | 명패 갱신, 이펙트 재생 |
| `src/PetApp/PetHost.cs` | `UsageTracker`를 30초 주기로 호출 |

---

### Task 1: 모델 단가와 비용 공식

**Files:**
- Create: `src/PetCore/UsagePricing.cs`
- Test: `tests/PetCore.Tests/UsagePricingTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces:
  - `public readonly record struct TokenCounts(long Input, long CacheCreation, long CacheRead, long Output)`
  - `public static decimal UsagePricing.CostUsd(string? model, TokenCounts tokens)`

- [ ] **Step 1: Write the failing test**

`tests/PetCore.Tests/UsagePricingTests.cs`:

```csharp
using PetCore;

public class UsagePricingTests
{
    [Fact]
    public void Opus5_AppliesInputAndOutputRates()
    {
        // input 1M x $5 + output 1M x $25 = $30
        var cost = UsagePricing.CostUsd("claude-opus-5",
            new TokenCounts(1_000_000, 0, 0, 1_000_000));

        Assert.Equal(30.0m, cost);
    }

    [Fact]
    public void CacheWriteIs125PercentOfInput_CacheReadIs10Percent()
    {
        // opus-5 input $5/1M -> write $6.25/1M, read $0.50/1M
        var cost = UsagePricing.CostUsd("claude-opus-5",
            new TokenCounts(0, 1_000_000, 1_000_000, 0));

        Assert.Equal(6.75m, cost);
    }

    [Fact]
    public void SonnetIsCheaperThanOpus_ForTheSameTokens()
    {
        var tokens = new TokenCounts(1_000_000, 0, 0, 1_000_000);

        Assert.True(UsagePricing.CostUsd("claude-sonnet-5", tokens)
                  < UsagePricing.CostUsd("claude-opus-5", tokens));
    }

    [Fact]
    public void UnknownModel_FallsBackToOpus5Rates()
    {
        // 새 모델이 나와도 레벨이 멈추면 안 된다 (스펙 §2.3).
        var tokens = new TokenCounts(1_000_000, 0, 0, 0);

        Assert.Equal(UsagePricing.CostUsd("claude-opus-5", tokens),
                     UsagePricing.CostUsd("claude-something-unreleased-7", tokens));
    }

    [Fact]
    public void NullModel_FallsBackToOpus5Rates()
    {
        var tokens = new TokenCounts(1_000_000, 0, 0, 0);

        Assert.Equal(5.0m, UsagePricing.CostUsd(null, tokens));
    }

    [Fact]
    public void ModelNameIsCaseInsensitive()
    {
        var tokens = new TokenCounts(1_000_000, 0, 0, 0);

        Assert.Equal(UsagePricing.CostUsd("claude-sonnet-5", tokens),
                     UsagePricing.CostUsd("CLAUDE-SONNET-5", tokens));
    }

    [Fact]
    public void ZeroTokens_CostNothing()
    {
        Assert.Equal(0m, UsagePricing.CostUsd("claude-opus-5", new TokenCounts(0, 0, 0, 0)));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PetCore.Tests/PetCore.Tests.csproj --filter FullyQualifiedName~UsagePricingTests`
Expected: 컴파일 실패 — `'UsagePricing'이라는 이름이 없습니다` / `The name 'UsagePricing' does not exist`

- [ ] **Step 3: Write minimal implementation**

`src/PetCore/UsagePricing.cs`:

```csharp
namespace PetCore;

/// <summary>
/// 한 번의 API 호출이 소비한 토큰 네 종류. 트랜스크립트의 message.usage 와 1:1 대응한다.
/// </summary>
public readonly record struct TokenCounts(
    long Input,
    long CacheCreation,
    long CacheRead,
    long Output);

/// <summary>
/// 토큰을 API 환산 달러로 바꾼다.
///
/// raw 토큰 합계를 쓰지 않는 이유는 스펙 §2.2에 있다 — 실측상 cache_read 가 전체 토큰의
/// 97.7%인데 output 대비 1/50 가격이다. 합계로 세면 일한 양이 아니라 캐시 읽기를 세는 것이고,
/// 긴 세션을 열어두는 것만으로 레벨이 오르는 파밍 경로가 열린다.
/// </summary>
public static class UsagePricing
{
    /// <summary>캐시 쓰기는 input 단가의 1.25배.</summary>
    private const decimal CacheWriteMultiplier = 1.25m;

    /// <summary>캐시 읽기는 input 단가의 0.10배.</summary>
    private const decimal CacheReadMultiplier = 0.10m;

    private const decimal PerMillion = 1_000_000m;

    private readonly record struct Rate(decimal InputPerMillion, decimal OutputPerMillion);

    private static readonly Rate Fallback = new(5.00m, 25.00m);   // claude-opus-5

    private static readonly Dictionary<string, Rate> Rates =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-opus-5"]              = new(5.00m, 25.00m),
            ["claude-opus-4-8"]            = new(5.00m, 25.00m),
            ["claude-fable-5"]             = new(10.00m, 50.00m),
            ["claude-sonnet-5"]            = new(2.00m, 10.00m),
            ["claude-haiku-4-5-20251001"]  = new(1.00m, 5.00m),
        };

    public static decimal CostUsd(string? model, TokenCounts tokens)
    {
        // 가격표에 없는 모델은 Opus 5 단가로 센다. 새 모델이 나왔을 때 레벨이 멈추는 것보다
        // 과대/과소 추정이 낫다 (스펙 §2.3).
        var rate = model is not null && Rates.TryGetValue(model, out var found) ? found : Fallback;

        var input = tokens.Input * rate.InputPerMillion;
        var write = tokens.CacheCreation * rate.InputPerMillion * CacheWriteMultiplier;
        var read  = tokens.CacheRead * rate.InputPerMillion * CacheReadMultiplier;
        var output = tokens.Output * rate.OutputPerMillion;

        return (input + write + read + output) / PerMillion;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PetCore.Tests/PetCore.Tests.csproj --filter FullyQualifiedName~UsagePricingTests`
Expected: `통과!` / `Passed!` — 실패 0, 통과 7

- [ ] **Step 5: Commit**

```bash
git add src/PetCore/UsagePricing.cs tests/PetCore.Tests/UsagePricingTests.cs
git commit -m "feat: price token usage in API-equivalent dollars"
```

---

### Task 2: usage 줄 파싱

**Files:**
- Create: `src/PetCore/UsageLineParser.cs`
- Test: `tests/PetCore.Tests/UsageLineParserTests.cs`

**Interfaces:**
- Consumes: `TokenCounts` (Task 1)
- Produces:
  - `public readonly record struct UsageRecord(string MessageId, string? Model, TokenCounts Tokens)`
  - `public static bool UsageLineParser.TryParse(string line, out UsageRecord record)`

- [ ] **Step 1: Write the failing test**

`tests/PetCore.Tests/UsageLineParserTests.cs`:

```csharp
using PetCore;

public class UsageLineParserTests
{
    private const string RealLine = """
    {"type":"assistant","uuid":"abc","message":{"id":"msg_011Ce5bQ","role":"assistant","model":"claude-opus-5","content":[{"type":"text","text":"hi"}],"usage":{"input_tokens":2,"cache_creation_input_tokens":933,"cache_read_input_tokens":469803,"output_tokens":920,"service_tier":"standard"}}}
    """;

    [Fact]
    public void ParsesAllFourTokenCountsAndTheModel()
    {
        Assert.True(UsageLineParser.TryParse(RealLine, out var r));

        Assert.Equal("msg_011Ce5bQ", r.MessageId);
        Assert.Equal("claude-opus-5", r.Model);
        Assert.Equal(2, r.Tokens.Input);
        Assert.Equal(933, r.Tokens.CacheCreation);
        Assert.Equal(469803, r.Tokens.CacheRead);
        Assert.Equal(920, r.Tokens.Output);
    }

    [Fact]
    public void RejectsLineWithoutUsage()
    {
        var line = """{"type":"user","message":{"role":"user","content":"hello"}}""";

        Assert.False(UsageLineParser.TryParse(line, out _));
    }

    [Fact]
    public void RejectsLineWithoutMessageId()
    {
        // id 가 없으면 중복 제거를 할 수 없다. 세지 않는 편이 두 번 세는 것보다 낫다.
        var line = """{"message":{"usage":{"input_tokens":5,"output_tokens":5}}}""";

        Assert.False(UsageLineParser.TryParse(line, out _));
    }

    [Fact]
    public void MissingTokenFieldsCountAsZero()
    {
        var line = """{"message":{"id":"msg_x","usage":{"output_tokens":7}}}""";

        Assert.True(UsageLineParser.TryParse(line, out var r));
        Assert.Equal(0, r.Tokens.Input);
        Assert.Equal(0, r.Tokens.CacheRead);
        Assert.Equal(7, r.Tokens.Output);
    }

    [Fact]
    public void MissingModelIsNull()
    {
        var line = """{"message":{"id":"msg_x","usage":{"output_tokens":1}}}""";

        Assert.True(UsageLineParser.TryParse(line, out var r));
        Assert.Null(r.Model);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"message\":")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"message\":\"a string, not an object\"}")]
    [InlineData("{\"message\":{\"usage\":\"also a string\"}}")]
    [InlineData("{\"message\":{\"id\":123,\"usage\":{\"output_tokens\":1}}}")]
    public void MalformedInputReturnsFalseAndNeverThrows(string line)
    {
        Assert.False(UsageLineParser.TryParse(line, out _));
    }

    [Fact]
    public void NonNumericTokenValueIsTreatedAsZero()
    {
        var line = """{"message":{"id":"msg_x","usage":{"output_tokens":"lots"}}}""";

        Assert.True(UsageLineParser.TryParse(line, out var r));
        Assert.Equal(0, r.Tokens.Output);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PetCore.Tests/PetCore.Tests.csproj --filter FullyQualifiedName~UsageLineParserTests`
Expected: 컴파일 실패 — `'UsageLineParser'이라는 이름이 없습니다`

- [ ] **Step 3: Write minimal implementation**

`src/PetCore/UsageLineParser.cs`:

```csharp
using System.Text.Json;

namespace PetCore;

/// <summary>API 호출 한 건. MessageId 로 중복을 제거한다.</summary>
public readonly record struct UsageRecord(string MessageId, string? Model, TokenCounts Tokens);

/// <summary>
/// 트랜스크립트 JSONL 한 줄에서 usage 를 뽑는다.
///
/// TranscriptParser 와 같은 규율을 따른다: 모든 탐색 지점에서 ValueKind 를 먼저 확인한다.
/// JsonElement.TryGetProperty 는 대상이 객체가 아니면 던진다.
/// </summary>
public static class UsageLineParser
{
    public static bool TryParse(string line, out UsageRecord record)
    {
        record = default;

        if (string.IsNullOrWhiteSpace(line))
            return false;

        // 이 메서드의 계약은 "절대 던지지 않는다"이다. 손상된 줄은 이 파일 어디에나 있을 수
        // 있고(부분 기록, 인코딩 깨짐), 그 한 줄 때문에 스캔 전체가 중단되어서는 안 된다.
        try
        {
            using var doc = JsonDocument.Parse(line);

            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("message", out var message)) return false;
            if (message.ValueKind != JsonValueKind.Object) return false;
            if (!message.TryGetProperty("usage", out var usage)) return false;
            if (usage.ValueKind != JsonValueKind.Object) return false;

            // id 가 없으면 중복 제거를 할 수 없으므로 세지 않는다. 두 번 세는 것보다 낫다.
            if (!message.TryGetProperty("id", out var idProp)) return false;
            if (idProp.ValueKind != JsonValueKind.String) return false;
            var id = idProp.GetString();
            if (string.IsNullOrEmpty(id)) return false;

            var model = message.TryGetProperty("model", out var m)
                        && m.ValueKind == JsonValueKind.String
                ? m.GetString()
                : null;

            record = new UsageRecord(id, model, new TokenCounts(
                Long(usage, "input_tokens"),
                Long(usage, "cache_creation_input_tokens"),
                Long(usage, "cache_read_input_tokens"),
                Long(usage, "output_tokens")));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static long Long(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.Number
        && v.TryGetInt64(out var n)
            ? n
            : 0;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PetCore.Tests/PetCore.Tests.csproj --filter FullyQualifiedName~UsageLineParserTests`
Expected: `통과!` — 실패 0, 통과 13

- [ ] **Step 5: Commit**

```bash
git add src/PetCore/UsageLineParser.cs tests/PetCore.Tests/UsageLineParserTests.cs
git commit -m "feat: parse token usage out of a transcript line"
```

---

### Task 3: 레벨 곡선

**Files:**
- Create: `src/PetCore/LevelCurve.cs`
- Test: `tests/PetCore.Tests/LevelCurveTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces:
  - `public static class LevelCurve` — 상수 `A`, `C0`, `C2`, `M3` (`const double`), 계산 속성 `M4`, `C3`, `C4` (`static double`)
  - `public static int LevelCurve.LevelFor(decimal costUsd)`

- [ ] **Step 1: Write the failing test**

`tests/PetCore.Tests/LevelCurveTests.cs`:

```csharp
using PetCore;

public class LevelCurveTests
{
    [Fact]
    public void DerivedConstantsAreComputedFromTheCanonicalFour()
    {
        // 스펙 §4.1: M4/C3/C4 를 하드코딩하면 M3 를 조정했을 때 경계가 따라오지 않는다.
        Assert.Equal(LevelCurve.M3 * 1.9, LevelCurve.M4, 6);
        Assert.Equal(LevelCurve.C2 + 900 * LevelCurve.M3, LevelCurve.C3, 6);
        Assert.Equal(LevelCurve.C3 + 8999 * LevelCurve.M4, LevelCurve.C4, 6);
    }

    [Fact]
    public void BelowTheFirstThreshold_ClampsToOne()
    {
        Assert.Equal(1, LevelCurve.LevelFor(0m));
        Assert.Equal(1, LevelCurve.LevelFor(1m));
        Assert.Equal(1, LevelCurve.LevelFor(3.60m));
    }

    [Fact]
    public void AtTheFirstThreshold_IsExactlyLevelOne()
    {
        Assert.Equal(1, LevelCurve.LevelFor((decimal)LevelCurve.C0));
    }

    [Fact]
    public void HitsTheDesignAnchors()
    {
        // 스펙 §4.2 의 두 앵커. $30 은 곡선을 맞출 때 쓴 근사 앵커라 원값이 29.9819 이고,
        // 내림하면 29 다 — 스펙 §4.3 의 페이스 표도 내림 기준이므로 일치한다.
        // $4,992 는 구간 경계라 정확히 100 이다.
        Assert.Equal(29, LevelCurve.LevelFor(30m));
        Assert.Equal(30, LevelCurve.LevelFor(30.10m));
        Assert.Equal(100, LevelCurve.LevelFor((decimal)LevelCurve.C2));
    }

    [Fact]
    public void HitsTheBandBoundaries()
    {
        Assert.Equal(1000, LevelCurve.LevelFor((decimal)LevelCurve.C3));
        Assert.Equal(9999, LevelCurve.LevelFor((decimal)LevelCurve.C4));
    }

    [Fact]
    public void AboveTheCeiling_ClampsTo9999()
    {
        Assert.Equal(9999, LevelCurve.LevelFor((decimal)LevelCurve.C4 * 10m));
    }

    [Fact]
    public void IsMonotonicAcrossBothBandBoundaries()
    {
        // 경계에서 레벨이 뒷걸음질치면 명패 숫자가 내려간다. 절대 그러면 안 된다.
        var previous = 0;
        foreach (var cost in new[] { 3.61m, 30m, 500m, 4991m, 4992m, 4993m,
                                     100_000m, 1_261_544m, 1_261_545m, 1_261_546m,
                                     5_000_000m, 25_133_372m })
        {
            var level = LevelCurve.LevelFor(cost);
            Assert.True(level >= previous, $"${cost} 에서 레벨이 {previous} → {level} 로 내려갔다");
            previous = level;
        }
    }

    [Fact]
    public void MatchesTheSpecPaceTable()
    {
        // 스펙 §4.3. 기준 사용자 $166.3947/일.
        const decimal perDay = 6323m / 38m;

        Assert.Equal(99, LevelCurve.LevelFor(perDay * 30));    // 1달
        Assert.Equal(139, LevelCurve.LevelFor(perDay * 365));  // 1년
        Assert.Equal(313, LevelCurve.LevelFor(perDay * 1825)); // 5년
    }

    [Fact]
    public void NegativeCostIsTreatedAsZero()
    {
        Assert.Equal(1, LevelCurve.LevelFor(-5m));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PetCore.Tests/PetCore.Tests.csproj --filter FullyQualifiedName~LevelCurveTests`
Expected: 컴파일 실패 — `'LevelCurve'이라는 이름이 없습니다`

- [ ] **Step 3: Write minimal implementation**

`src/PetCore/LevelCurve.cs`:

```csharp
namespace PetCore;

/// <summary>
/// 누적 비용(API 환산 달러)을 레벨로 바꾼다. 스펙 §4.
///
/// 구간 1은 로그, 구간 2·3은 선형이다. 구간 2를 로그로 두면 L100 을 넘는 순간 레벨 값이
/// 23배 싸진다 — 구간 2는 레벨이 900개인데 비용 범위는 구간 1보다 좁기 때문이다. 한 달
/// 걸려 L100 을 찍자마자 L101 부터 우수수 오르는 모양이 되므로 선형을 쓴다.
/// </summary>
public static class LevelCurve
{
    // --- 정본 상수. 이 넷만 손으로 정한다. ---
    public const double A  = 13.6869;   // 구간 1 로그 계수
    public const double C0 = 3.61;      // L1 문턱
    public const double C2 = 4992.0;    // L100 (= 기준 사용자 한 달)
    public const double M3 = 1396.17;   // 구간 2 레벨당 달러

    // --- 유도 상수. 하드코딩하지 않는다 (스펙 §4.1). ---
    public static double M4 => M3 * 1.9;
    public static double C3 => C2 + 900 * M3;
    public static double C4 => C3 + 8999 * M4;

    public const int MinLevel = 1;
    public const int MaxLevel = 9999;

    public static int LevelFor(decimal costUsd)
    {
        if (costUsd <= 0m) return MinLevel;

        var c = (double)costUsd;

        double level;
        if (c < C0)       level = MinLevel;
        else if (c < C2)  level = 1 + A * Math.Log(c / C0);
        else if (c < C3)  level = 100 + (c - C2) / M3;
        else              level = 1000 + (c - C3) / M4;

        // 부동소수 오차로 경계에서 99.9999 가 나오면 내림 때문에 99 가 된다. 앵커가
        // 정확히 떨어지도록 아주 작은 값을 더한 뒤 내림한다.
        var rounded = (int)Math.Floor(level + 1e-9);

        return Math.Clamp(rounded, MinLevel, MaxLevel);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PetCore.Tests/PetCore.Tests.csproj --filter FullyQualifiedName~LevelCurveTests`
Expected: `통과!` — 실패 0, 통과 9

- [ ] **Step 5: Commit**

```bash
git add src/PetCore/LevelCurve.cs tests/PetCore.Tests/LevelCurveTests.cs
git commit -m "feat: map cumulative cost to a level with the three-band curve"
```

---

### Task 4: 파일 하나 스캔 + 중복 제거

**Files:**
- Create: `src/PetCore/TranscriptCostScanner.cs`
- Test: `tests/PetCore.Tests/TranscriptCostScannerTests.cs`

**Interfaces:**
- Consumes: `UsageLineParser.TryParse` (Task 2), `UsagePricing.CostUsd` (Task 1)
- Produces: `public decimal TranscriptCostScanner.ScanFile(string path)` — 절대 던지지 않는다. 읽을 수 없으면 `0m`.

- [ ] **Step 1: Write the failing test**

`tests/PetCore.Tests/TranscriptCostScannerTests.cs`:

```csharp
using PetCore;

public class TranscriptCostScannerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pet-scan-" + Guid.NewGuid().ToString("N"));

    public TranscriptCostScannerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string WriteFile(string name, params string[] lines)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllLines(path, lines);
        return path;
    }

    private static string Line(string id, string model, long output) =>
        $$$$"""{"message":{"id":"{{{{id}}}}","model":"{{{{model}}}}","usage":{"input_tokens":0,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":{{{{output}}}}}}}""";

    [Fact]
    public void SumsCostAcrossDistinctMessages()
    {
        // opus-5 output $25/1M. 1M + 1M = $50.
        var path = WriteFile("a.jsonl",
            Line("msg_1", "claude-opus-5", 1_000_000),
            Line("msg_2", "claude-opus-5", 1_000_000));

        Assert.Equal(50m, new TranscriptCostScanner().ScanFile(path));
    }

    [Fact]
    public void CountsARepeatedMessageIdExactlyOnce()
    {
        // 실측: usage 줄의 56.9%가 중복이다. 한 응답이 content 블록별로 여러 줄에 기록되고
        // 모든 줄이 같은 message.id 와 동일한 usage 를 담는다 (스펙 §3.2).
        var path = WriteFile("dup.jsonl",
            Line("msg_1", "claude-opus-5", 1_000_000),
            Line("msg_1", "claude-opus-5", 1_000_000),
            Line("msg_1", "claude-opus-5", 1_000_000));

        Assert.Equal(25m, new TranscriptCostScanner().ScanFile(path));
    }

    [Fact]
    public void DeduplicatesEvenWhenRepeatsAreFarApart()
    {
        // 실측: 같은 id 가 최대 6,416줄 떨어져 다시 나온다. 인접 줄만 보는 방식으로는 못 잡는다.
        var lines = new List<string> { Line("msg_far", "claude-opus-5", 1_000_000) };
        for (var i = 0; i < 500; i++)
            lines.Add("""{"type":"user","message":{"role":"user","content":"filler"}}""");
        lines.Add(Line("msg_far", "claude-opus-5", 1_000_000));

        var path = WriteFile("far.jsonl", lines.ToArray());

        Assert.Equal(25m, new TranscriptCostScanner().ScanFile(path));
    }

    [Fact]
    public void SkipsMalformedLinesAndKeepsGoing()
    {
        var path = WriteFile("mixed.jsonl",
            "not json",
            Line("msg_1", "claude-opus-5", 1_000_000),
            "{\"message\":",
            "",
            Line("msg_2", "claude-opus-5", 1_000_000));

        Assert.Equal(50m, new TranscriptCostScanner().ScanFile(path));
    }

    [Fact]
    public void MissingFileReturnsZeroAndDoesNotThrow()
    {
        Assert.Equal(0m, new TranscriptCostScanner().ScanFile(Path.Combine(_dir, "nope.jsonl")));
    }

    [Fact]
    public void EmptyFileReturnsZero()
    {
        Assert.Equal(0m, new TranscriptCostScanner().ScanFile(WriteFile("empty.jsonl")));
    }

    [Fact]
    public void ReadsAFileThatIsOpenForWritingElsewhere()
    {
        // Claude Code 가 지금 쓰고 있는 파일을 읽어야 한다. 잠그면 안 된다.
        var path = WriteFile("live.jsonl", Line("msg_1", "claude-opus-5", 1_000_000));

        using var writer = new FileStream(path, FileMode.Append, FileAccess.Write,
                                          FileShare.ReadWrite | FileShare.Delete);

        Assert.Equal(25m, new TranscriptCostScanner().ScanFile(path));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PetCore.Tests/PetCore.Tests.csproj --filter FullyQualifiedName~TranscriptCostScannerTests`
Expected: 컴파일 실패 — `'TranscriptCostScanner'이라는 이름이 없습니다`

- [ ] **Step 3: Write minimal implementation**

`src/PetCore/TranscriptCostScanner.cs`:

```csharp
using System.Text;

namespace PetCore;

/// <summary>
/// 트랜스크립트 파일 하나의 누적 비용을 계산한다.
///
/// 중복 제거용 id 집합은 이 메서드 안에서만 살아 있다가 반환과 함께 버려진다. 전체를 기억하면
/// 34,254개 x 약 100 bytes = 3.4 MB 이고 1년이면 40 MB 가 된다 — 펫 전체가 75 MB 인데 그
/// 절반이 id 목록이 되므로 그렇게 하지 않는다 (스펙 §3.3).
/// </summary>
public sealed class TranscriptCostScanner
{
    /// <summary>
    /// 절대 던지지 않는다. 읽을 수 없으면 0을 돌려준다.
    ///
    /// catch-all 은 의도적이다. 이 파일은 Claude Code 가 지금 쓰고 있을 수 있고, 스캔 도중
    /// 지워질 수도 있으며, 권한이 바뀔 수도 있다. 예외 종류를 하나씩 열거하는 방식은 이
    /// 저장소에서 이미 세 번 실패했다.
    /// </summary>
    public decimal ScanFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return 0m;

            // FileShare.ReadWrite | Delete: 쓰는 쪽을 절대 막지 않는다. TranscriptTail 과
            // 같은 계약이다 (설계서 §6.2).
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var total = 0m;

            while (reader.ReadLine() is { } line)
            {
                // usage 없는 줄이 대부분이다. JSON 파싱 전에 값싸게 걸러낸다.
                if (line.Length == 0 || !line.Contains("\"usage\"", StringComparison.Ordinal))
                    continue;

                if (!UsageLineParser.TryParse(line, out var record))
                    continue;

                if (!seen.Add(record.MessageId))
                    continue;

                total += UsagePricing.CostUsd(record.Model, record.Tokens);
            }

            return total;
        }
        catch (Exception)
        {
            return 0m;
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PetCore.Tests/PetCore.Tests.csproj --filter FullyQualifiedName~TranscriptCostScannerTests`
Expected: `통과!` — 실패 0, 통과 7

- [ ] **Step 5: Commit**

```bash
git add src/PetCore/TranscriptCostScanner.cs tests/PetCore.Tests/TranscriptCostScannerTests.cs
git commit -m "feat: scan one transcript for cost, deduplicating by message id"
```

---

### Task 5: 상태 파일

**Files:**
- Create: `src/PetCore/UsageState.cs`
- Create: `src/PetCore/UsageStore.cs`
- Test: `tests/PetCore.Tests/UsageStoreTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces:
  - `public sealed class UsageFileEntry { public long Size; public long MtimeUnixMs; public decimal CostUsd; }` (기본 생성자 + set 가능한 속성)
  - `public sealed class UsageState { public int Version; public decimal TotalCostUsd; public int Level; public Dictionary<string, UsageFileEntry> Files; }`
  - `public sealed class UsageStore(string dataDir)` — `UsageState Load()`, `void Save(UsageState state)`. 둘 다 절대 던지지 않는다.
  - `public const int UsageState.CurrentVersion = 1`

- [ ] **Step 1: Write the failing test**

`tests/PetCore.Tests/UsageStoreTests.cs`:

```csharp
using PetCore;

public class UsageStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pet-store-" + Guid.NewGuid().ToString("N"));

    public UsageStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string StatePath => Path.Combine(_dir, "usage.json");

    [Fact]
    public void LoadOnAnEmptyDirectoryReturnsAFreshState()
    {
        var state = new UsageStore(_dir).Load();

        Assert.Equal(UsageState.CurrentVersion, state.Version);
        Assert.Equal(0m, state.TotalCostUsd);
        Assert.Equal(0, state.Level);
        Assert.Empty(state.Files);
    }

    [Fact]
    public void SaveThenLoadRoundTrips()
    {
        var store = new UsageStore(_dir);
        var state = new UsageState { TotalCostUsd = 6323.23m, Level = 99 };
        state.Files["C:/x/a.jsonl"] = new UsageFileEntry { Size = 20695541, MtimeUnixMs = 1756400000000, CostUsd = 412.83m };

        store.Save(state);
        var loaded = store.Load();

        Assert.Equal(6323.23m, loaded.TotalCostUsd);
        Assert.Equal(99, loaded.Level);
        var entry = Assert.Single(loaded.Files);
        Assert.Equal("C:/x/a.jsonl", entry.Key);
        Assert.Equal(20695541, entry.Value.Size);
        Assert.Equal(1756400000000, entry.Value.MtimeUnixMs);
        Assert.Equal(412.83m, entry.Value.CostUsd);
    }

    [Fact]
    public void CorruptFileYieldsAFreshStateInsteadOfThrowing()
    {
        File.WriteAllText(StatePath, "{ this is not json");

        var state = new UsageStore(_dir).Load();

        Assert.Equal(0m, state.TotalCostUsd);
        Assert.Empty(state.Files);
    }

    [Fact]
    public void UnknownVersionYieldsAFreshState()
    {
        // 형식이 바뀌면 옛 파일을 해석하려 들지 말고 다시 스캔한다. 1초면 된다.
        File.WriteAllText(StatePath, """{"version":999,"totalCostUsd":1,"level":1,"files":{}}""");

        var state = new UsageStore(_dir).Load();

        Assert.Equal(UsageState.CurrentVersion, state.Version);
        Assert.Equal(0m, state.TotalCostUsd);
    }

    [Fact]
    public void SaveCreatesTheDirectoryIfItIsMissing()
    {
        var nested = Path.Combine(_dir, "does", "not", "exist");

        new UsageStore(nested).Save(new UsageState { Level = 7 });

        Assert.Equal(7, new UsageStore(nested).Load().Level);
    }

    [Fact]
    public void SaveIsAtomic_NoTempFileIsLeftBehind()
    {
        var store = new UsageStore(_dir);
        store.Save(new UsageState { Level = 3 });

        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void SaveToAnUnwritablePathDoesNotThrow()
    {
        // 파일 이름이 될 수 없는 경로. 저장 실패가 펫을 죽여서는 안 된다.
        var store = new UsageStore(Path.Combine(_dir, "usage.json"));   // 파일을 디렉터리로 넘김
        File.WriteAllText(Path.Combine(_dir, "usage.json"), "x");

        store.Save(new UsageState { Level = 1 });   // 던지지 않으면 통과
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PetCore.Tests/PetCore.Tests.csproj --filter FullyQualifiedName~UsageStoreTests`
Expected: 컴파일 실패 — `'UsageStore'이라는 이름이 없습니다`

- [ ] **Step 3: Write minimal implementation**

`src/PetCore/UsageState.cs`:

```csharp
namespace PetCore;

/// <summary>트랜스크립트 파일 하나에 대해 기억해 두는 것.</summary>
public sealed class UsageFileEntry
{
    public long Size { get; set; }
    public long MtimeUnixMs { get; set; }
    public decimal CostUsd { get; set; }
}

/// <summary>
/// 상태 파일의 내용. 파일별 (크기, mtime, 비용)을 기억해 두었다가, 다음 기동 때 값이 그대로면
/// 그 파일을 아예 읽지 않는다 (스펙 §7.2).
/// </summary>
public sealed class UsageState
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public decimal TotalCostUsd { get; set; }
    public int Level { get; set; }
    public Dictionary<string, UsageFileEntry> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
```

`src/PetCore/UsageStore.cs`:

```csharp
using System.Text.Json;

namespace PetCore;

/// <summary>
/// 상태 파일을 읽고 쓴다. Load 도 Save 도 절대 던지지 않는다 — 레벨 표시가 실패했다고
/// 장식용 펫이 죽어서는 안 된다.
/// </summary>
public sealed class UsageStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private readonly string _dataDir;

    public UsageStore(string dataDir) => _dataDir = dataDir;

    private string Path_ => Path.Combine(_dataDir, "usage.json");

    /// <summary>읽을 수 없거나 형식이 다르면 빈 상태를 돌려준다. 전량 재스캔은 1초면 된다.</summary>
    public UsageState Load()
    {
        try
        {
            if (!File.Exists(Path_)) return new UsageState();

            using var stream = new FileStream(
                Path_, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            var state = JsonSerializer.Deserialize<UsageState>(stream, Options);

            if (state is null || state.Version != UsageState.CurrentVersion)
                return new UsageState();

            state.Files ??= new Dictionary<string, UsageFileEntry>(StringComparer.OrdinalIgnoreCase);
            return state;
        }
        catch (Exception)
        {
            return new UsageState();
        }
    }

    /// <summary>임시 파일에 쓴 뒤 옮긴다. 도중에 죽어도 반쯤 쓰인 파일이 남지 않는다.</summary>
    public void Save(UsageState state)
    {
        try
        {
            Directory.CreateDirectory(_dataDir);

            var temp = Path_ + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, Options));
            File.Move(temp, Path_, overwrite: true);
        }
        catch (Exception)
        {
            // 저장 실패는 다음 주기에 다시 시도된다. 그 사이에는 메모리의 값을 쓴다.
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PetCore.Tests/PetCore.Tests.csproj --filter FullyQualifiedName~UsageStoreTests`
Expected: `통과!` — 실패 0, 통과 7

- [ ] **Step 5: Commit**

```bash
git add src/PetCore/UsageState.cs src/PetCore/UsageStore.cs tests/PetCore.Tests/UsageStoreTests.cs
git commit -m "feat: persist per-file usage totals so restarts do not rescan"
```

---

### Task 6: UsageTracker 조립

**Files:**
- Create: `src/PetCore/UsageTracker.cs`
- Test: `tests/PetCore.Tests/UsageTrackerTests.cs`

**Interfaces:**
- Consumes: `UsageStore` (Task 5), `TranscriptCostScanner` (Task 4), `LevelCurve.LevelFor` (Task 3)
- Produces:
  - `public readonly record struct UsageSnapshot(decimal TotalCostUsd, int Level, bool LeveledUp)`
  - `public sealed class UsageTracker(string projectsRoot, UsageStore store, TranscriptCostScanner scanner)`
  - `public UsageSnapshot Refresh()` — 절대 던지지 않는다

- [ ] **Step 1: Write the failing test**

`tests/PetCore.Tests/UsageTrackerTests.cs`:

```csharp
using PetCore;

public class UsageTrackerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pet-track-" + Guid.NewGuid().ToString("N"));
    private readonly string _data;

    public UsageTrackerTests()
    {
        _data = Path.Combine(_root, "data");
        Directory.CreateDirectory(Path.Combine(_root, "projects", "proj-a"));
        Directory.CreateDirectory(_data);
    }
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private string ProjectsRoot => Path.Combine(_root, "projects");

    private static string Line(string id, long output) =>
        $$$$"""{"message":{"id":"{{{{id}}}}","model":"claude-opus-5","usage":{"output_tokens":{{{{output}}}}}}}""";

    private string WriteTranscript(string project, string name, params string[] lines)
    {
        var dir = Path.Combine(ProjectsRoot, project);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllLines(path, lines);
        return path;
    }

    private UsageTracker NewTracker() =>
        new(ProjectsRoot, new UsageStore(_data), new TranscriptCostScanner());

    [Fact]
    public void SumsEveryTranscriptUnderTheRoot()
    {
        WriteTranscript("proj-a", "s1.jsonl", Line("m1", 1_000_000));   // $25
        WriteTranscript("proj-b", "s2.jsonl", Line("m2", 1_000_000));   // $25

        var snap = NewTracker().Refresh();

        Assert.Equal(50m, snap.TotalCostUsd);
    }

    [Fact]
    public void FindsTranscriptsInNestedSubagentDirectories()
    {
        WriteTranscript(Path.Combine("proj-a", "s1", "subagents"), "agent-x.jsonl", Line("m1", 1_000_000));

        Assert.Equal(25m, NewTracker().Refresh().TotalCostUsd);
    }

    [Fact]
    public void ReportsTheLevelForTheTotal()
    {
        // opus-5 output $25/1M 이므로 1.2M 토큰 = $30. 원값 29.9819 -> 내림 29.
        WriteTranscript("proj-a", "s1.jsonl", Line("m1", 1_200_000));

        var snap = NewTracker().Refresh();

        Assert.Equal(30m, snap.TotalCostUsd);
        Assert.Equal(LevelCurve.LevelFor(30m), snap.Level);
        Assert.Equal(29, snap.Level);
    }

    [Fact]
    public void UnchangedFilesAreNotRescanned()
    {
        var path = WriteTranscript("proj-a", "s1.jsonl", Line("m1", 1_000_000));

        var store = new UsageStore(_data);
        new UsageTracker(ProjectsRoot, store, new TranscriptCostScanner()).Refresh();

        // 스캐너를 세는 대역으로 바꿔 끼고, 두 번째 Refresh 가 파일을 읽지 않는지 본다.
        var counting = new CountingScanner();
        var snap = new UsageTracker(ProjectsRoot, store, counting).Refresh();

        Assert.Equal(0, counting.Calls);
        Assert.Equal(25m, snap.TotalCostUsd);
    }

    [Fact]
    public void AGrownFileIsRescannedAndTheTotalUpdates()
    {
        var path = WriteTranscript("proj-a", "s1.jsonl", Line("m1", 1_000_000));
        var store = new UsageStore(_data);
        new UsageTracker(ProjectsRoot, store, new TranscriptCostScanner()).Refresh();

        File.AppendAllLines(path, new[] { Line("m2", 1_000_000) });

        var snap = new UsageTracker(ProjectsRoot, store, new TranscriptCostScanner()).Refresh();

        Assert.Equal(50m, snap.TotalCostUsd);
    }

    [Fact]
    public void DisappearedFilesDropOutOfTheTotal()
    {
        var path = WriteTranscript("proj-a", "s1.jsonl", Line("m1", 1_000_000));
        WriteTranscript("proj-a", "s2.jsonl", Line("m2", 1_000_000));
        var store = new UsageStore(_data);
        new UsageTracker(ProjectsRoot, store, new TranscriptCostScanner()).Refresh();

        File.Delete(path);

        Assert.Equal(25m, new UsageTracker(ProjectsRoot, store, new TranscriptCostScanner()).Refresh().TotalCostUsd);
    }

    [Fact]
    public void LeveledUpIsFalseOnTheVeryFirstRefresh()
    {
        // 처음 켰을 때 그동안 쌓인 레벨로 이펙트가 터지면 안 된다.
        WriteTranscript("proj-a", "s1.jsonl", Line("m1", 100_000_000));

        Assert.False(NewTracker().Refresh().LeveledUp);
    }

    [Fact]
    public void LeveledUpIsTrueOnlyWhenTheLevelActuallyRises()
    {
        var path = WriteTranscript("proj-a", "s1.jsonl", Line("m1", 1_200_000));   // $30 -> L30
        var store = new UsageStore(_data);
        new UsageTracker(ProjectsRoot, store, new TranscriptCostScanner()).Refresh();

        // 같은 내용으로 다시 -> 레벨 그대로
        Assert.False(new UsageTracker(ProjectsRoot, store, new TranscriptCostScanner()).Refresh().LeveledUp);

        // 비용을 늘려 레벨이 오르게 한다
        File.AppendAllLines(path, new[] { Line("m2", 4_000_000) });
        Assert.True(new UsageTracker(ProjectsRoot, store, new TranscriptCostScanner()).Refresh().LeveledUp);
    }

    [Fact]
    public void MissingProjectsRootReturnsLevelOneAndDoesNotThrow()
    {
        var tracker = new UsageTracker(Path.Combine(_root, "nope"), new UsageStore(_data), new TranscriptCostScanner());

        var snap = tracker.Refresh();

        Assert.Equal(0m, snap.TotalCostUsd);
        Assert.Equal(1, snap.Level);
        Assert.False(snap.LeveledUp);
    }

    private sealed class CountingScanner : TranscriptCostScanner
    {
        public int Calls;
        public new decimal ScanFile(string path) { Calls++; return 0m; }
    }
}
```

> **주의:** `CountingScanner`가 `new` 로 가리기만 하면 `UsageTracker` 안에서 호출되지 않는다.
> 구현 단계에서 `TranscriptCostScanner.ScanFile` 을 `virtual` 로 만들고 테스트는 `override` 로
> 바꾼다. 아래 Step 3 에 그 변경이 포함되어 있다.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PetCore.Tests/PetCore.Tests.csproj --filter FullyQualifiedName~UsageTrackerTests`
Expected: 컴파일 실패 — `'UsageTracker'이라는 이름이 없습니다`

- [ ] **Step 3: Write minimal implementation**

먼저 `src/PetCore/TranscriptCostScanner.cs` 의 시그니처를 `virtual` 로 바꾼다:

```csharp
    public virtual decimal ScanFile(string path)
```

그리고 테스트의 `CountingScanner` 를 `override` 로 고친다:

```csharp
    private sealed class CountingScanner : TranscriptCostScanner
    {
        public int Calls;
        public override decimal ScanFile(string path) { Calls++; return 0m; }
    }
```

`src/PetCore/UsageTracker.cs`:

```csharp
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
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PetCore.Tests/PetCore.Tests.csproj`
Expected: `통과!` — 실패 0. 기존 96개 + 신규 43개 = 통과 139

- [ ] **Step 5: Commit**

```bash
git add src/PetCore/UsageTracker.cs src/PetCore/TranscriptCostScanner.cs tests/PetCore.Tests/UsageTrackerTests.cs
git commit -m "feat: track cumulative usage across transcripts, rescanning only what changed"
```

---

### Task 7: 명패 렌더러

**Files:**
- Create: `src/PetApp/PlateRenderer.cs`

**Interfaces:**
- Consumes: 없음
- Produces:
  - `internal static class PlateRenderer`
  - `public const int GapPx = 6` — 판 오른쪽 끝과 펫 왼쪽 첫 픽셀 사이 (스프라이트 좌표계)
  - `public static int PlateWidthFor(int level)`
  - `public const int PlateHeight = 9`
  - `public static BitmapSource Render(int level)` — 1x 스프라이트 좌표계 비트맵

`PetApp` 에는 테스트 프로젝트가 없다(WPF·창 핸들 의존). 검증은 Task 9 의 육안 확인으로 한다.

- [ ] **Step 1: 명패 렌더러 작성**

`src/PetApp/PlateRenderer.cs`:

```csharp
using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PetApp;

/// <summary>
/// 레벨 명패를 그린다. 펫 왼쪽에 놓이는 단색 판 + 3x5 픽셀 숫자.
///
/// 왜 판을 그리는가 — 마크는 몸통이 아니라 그 위의 투명한 공간에 뜨므로 배경화면이 바로 뒤에
/// 보인다. 외곽선만 두른 맨 숫자는 대부분의 배경에서 읽히지만, 판은 모든 배경에서 읽힌다.
/// 판이 명도의 양 극단을 다 갖고 있어서 밝은 배경에서는 검은 바탕이, 어두운 배경에서는 밝은
/// 테두리가 분리를 만든다 (스펙 §5.2).
///
/// 왜 왼쪽인가 — 빠직(MARK_CENTER_X = 23)과 물음표가 셀 오른쪽 위를 쓴다. 오른쪽이나 머리
/// 위에 두면 Blocked / YourTurn 상태에서 겹친다 (스펙 §5.3).
/// </summary>
internal static class PlateRenderer
{
    /// <summary>판 오른쪽 끝과 펫 왼쪽 첫 픽셀(LEFT_NUB_X0 = 4) 사이의 간격.</summary>
    public const int GapPx = 6;

    public const int PlateHeight = 9;

    private const int DigitWidth = 3;
    private const int DigitHeight = 5;
    private const int DigitSpacing = 1;
    private const int PaddingX = 2;
    private const int PaddingY = 2;

    private static readonly Color Fill   = Color.FromRgb(0x12, 0x11, 0x16);
    private static readonly Color Edge   = Color.FromRgb(0xEE, 0xEA, 0xE2);
    private static readonly Color Ink    = Color.FromRgb(0xFF, 0xFD, 0xF8);

    /// <summary>3x5 픽셀 숫자. 각 문자열 한 줄이 한 행이고 '1'이 켜진 픽셀이다.</summary>
    private static readonly string[][] Glyphs =
    {
        new[] { "111", "101", "101", "101", "111" }, // 0
        new[] { "010", "110", "010", "010", "111" }, // 1
        new[] { "111", "001", "111", "100", "111" }, // 2
        new[] { "111", "001", "111", "001", "111" }, // 3
        new[] { "101", "101", "111", "001", "001" }, // 4
        new[] { "111", "100", "111", "001", "111" }, // 5
        new[] { "111", "100", "111", "101", "111" }, // 6
        new[] { "111", "001", "001", "001", "001" }, // 7
        new[] { "111", "101", "111", "101", "111" }, // 8
        new[] { "111", "101", "111", "001", "111" }, // 9
    };

    /// <summary>자릿수에 따라 폭이 변한다. 자릿수가 느는 순간은 평생 두 번뿐이다 (스펙 §5.4).</summary>
    public static int PlateWidthFor(int level)
    {
        var digits = Math.Max(1, level.ToString().Length);
        var inner = digits * DigitWidth + (digits - 1) * DigitSpacing;
        return inner + PaddingX * 2;
    }

    public static BitmapSource Render(int level)
    {
        var text = Math.Clamp(level, 1, 9999).ToString();
        var width = PlateWidthFor(level);
        var height = PlateHeight;

        var pixels = new uint[width * height];

        void Set(int x, int y, Color c)
        {
            if (x < 0 || y < 0 || x >= width || y >= height) return;
            pixels[y * width + x] = (uint)((0xFFu << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B);
        }

        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                Set(x, y, Fill);

        for (var x = 0; x < width; x++) { Set(x, 0, Edge); Set(x, height - 1, Edge); }
        for (var y = 0; y < height; y++) { Set(0, y, Edge); Set(width - 1, y, Edge); }

        var cursorX = PaddingX;
        foreach (var ch in text)
        {
            var glyph = Glyphs[ch - '0'];
            for (var gy = 0; gy < DigitHeight; gy++)
                for (var gx = 0; gx < DigitWidth; gx++)
                    if (glyph[gy][gx] == '1')
                        Set(cursorX + gx, PaddingY + gy, Ink);

            cursorX += DigitWidth + DigitSpacing;
        }

        var bitmap = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, null,
            pixels, width * 4);
        bitmap.Freeze();   // 렌더 스레드에서 안전하게 쓰려면 얼려야 한다
        return bitmap;
    }
}
```

- [ ] **Step 2: 빌드 확인**

Run: `dotnet build src/PetApp/PetApp.csproj -c Release`
Expected: `빌드했습니다.` — 경고 0, 오류 0

- [ ] **Step 3: Commit**

```bash
git add src/PetApp/PlateRenderer.cs
git commit -m "feat: draw the level plate as a pixel bitmap"
```

---

### Task 8: 레벨업 이펙트 스프라이트

**Files:**
- Create: `tools/spritegen/flashgen.py`
- Create: `src/PetApp/assets/flash.png` (생성물)
- Modify: `src/PetApp/PetApp.csproj` — `<Resource Include="assets\flash.png" />` 추가

**Interfaces:**
- Consumes: 없음
- Produces: `src/PetApp/assets/flash.png` — 256×32, 8프레임 × 32×32

- [ ] **Step 1: 생성 스크립트 작성**

`tools/spritegen/flashgen.py`:

```python
"""레벨업 이펙트 스프라이트를 만든다.

몸통 실루엣에서 링이 바깥으로 퍼지며 사라진다. 8프레임, 12fps 로 약 0.66초.

32x32 안에 들어간다: 몸통이 x 4..27 이라 좌우 4px 여유가 있고 링 3px 이 맞는다.
아래쪽은 발이 바닥(y=31)에 붙어 있어 여유가 없으므로 링이 바닥에서 잘린다 —
빛이 바닥을 뚫지 않는 것이 오히려 자연스럽다 (스펙 §6).

spritegen.py 와 같은 무의존 PNG 작성기를 쓴다.
"""
import struct
import zlib
from pathlib import Path

FRAME = 32
COLS = 8
W, H = FRAME * COLS, FRAME

# pet.png 의 서 있는 포즈 지오메트리와 맞춘다.
BODY_X0, BODY_X1 = 6, 25
BODY_Y0, BODY_Y1 = 15, 27
LEFT_NUB_X0, LEFT_NUB_X1 = 4, 5
RIGHT_NUB_X0, RIGHT_NUB_X1 = 26, 27
NUB_Y0, NUB_Y1 = 21, 24
LEG_Y0, LEG_Y1 = 28, 31
LEG_X0S = (7, 11, 19, 23)

# 링 색: 따뜻한 흰색에서 산호색으로 식는다. 알파로 사라진다.
RING = (255, 248, 232)

# 프레임별 (실루엣에서 바깥으로 밀어낸 거리, 알파)
# f0 은 실루엣에 딱 붙고, f3 에서 가장 크고, f7 에서 사라진다.
EXPAND = (0, 1, 2, 3, 4, 5, 6, 7)
ALPHA  = (255, 255, 230, 190, 145, 100, 55, 20)


def blank():
    return [[(0, 0, 0, 0)] * W for _ in range(H)]


def silhouette():
    """서 있는 포즈가 채우는 셀 좌표 집합."""
    cells = set()
    for y in range(BODY_Y0, BODY_Y1 + 1):
        for x in range(BODY_X0, BODY_X1 + 1):
            cells.add((x, y))
    for y in range(NUB_Y0, NUB_Y1 + 1):
        for x in range(LEFT_NUB_X0, LEFT_NUB_X1 + 1):
            cells.add((x, y))
        for x in range(RIGHT_NUB_X0, RIGHT_NUB_X1 + 1):
            cells.add((x, y))
    for x0 in LEG_X0S:
        for y in range(LEG_Y0, LEG_Y1 + 1):
            cells.add((x0, y))
            cells.add((x0 + 1, y))
    return cells


def ring_at(cells, distance):
    """실루엣에서 정확히 distance 만큼 떨어진 껍질(체비쇼프 거리)."""
    if distance == 0:
        # 실루엣 바로 바깥 한 겹.
        distance = 1
    inner = grow(cells, distance - 1)
    outer = grow(cells, distance)
    return outer - inner


def grow(cells, distance):
    """체비쇼프 거리 distance 만큼 부풀린 집합."""
    out = set()
    for (x, y) in cells:
        for dy in range(-distance, distance + 1):
            for dx in range(-distance, distance + 1):
                out.add((x + dx, y + dy))
    return out


def write_png(path, px):
    raw = b"".join(
        b"\x00" + b"".join(struct.pack("4B", *px[y][x]) for x in range(W))
        for y in range(H)
    )

    def chunk(tag, data):
        c = tag + data
        return struct.pack(">I", len(data)) + c + struct.pack(">I", zlib.crc32(c))

    png = (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", W, H, 8, 6, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(raw, 9))
        + chunk(b"IEND", b"")
    )
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(png)


def main():
    px = blank()
    body = silhouette()
    for col in range(COLS):
        ox = col * FRAME
        alpha = ALPHA[col]
        for (x, y) in ring_at(body, EXPAND[col]):
            # 셀 밖으로 나간 픽셀은 버린다. 아래쪽은 바닥에서 잘린다.
            if 0 <= x < FRAME and 0 <= y < FRAME:
                px[y][ox + x] = (*RING, alpha)

    out = Path(__file__).resolve().parents[2] / "src" / "PetApp" / "assets" / "flash.png"
    write_png(out, px)
    print(f"wrote {out} ({W}x{H})")


if __name__ == "__main__":
    main()
```

- [ ] **Step 2: 생성하고 결과를 눈으로 확인**

```bash
cd "c:/Users/hayoul1999.YOUL-HOUSE/Desktop/github/claude-pet"
python tools/spritegen/flashgen.py
```

Expected: `wrote .../src/PetApp/assets/flash.png (256x32)`

생성된 그림을 확인한다 — 8프레임이 왼쪽에서 오른쪽으로 갈수록 링이 커지고 흐려져야 하고,
어느 프레임도 셀 경계를 넘어 옆 프레임을 침범하면 안 된다.

```bash
python -c "
from PIL import Image
im = Image.open('src/PetApp/assets/flash.png')
print(im.size, im.mode)
for c in range(8):
    f = im.crop((c*32,0,c*32+32,32))
    n = sum(1 for p in f.getdata() if p[3] > 0)
    print(f'frame {c}: {n} px, max alpha {max(p[3] for p in f.getdata())}')
"
```

Expected: 프레임 0→7 로 갈수록 픽셀 수가 늘고 최대 알파가 줄어든다.

- [ ] **Step 3: 리소스 등록**

`src/PetApp/PetApp.csproj` 의 `<ItemGroup>` 을 고친다:

```xml
  <ItemGroup>
    <Resource Include="assets\pet.png" />
    <Resource Include="assets\flash.png" />
  </ItemGroup>
```

- [ ] **Step 4: 빌드 확인**

Run: `dotnet build src/PetApp/PetApp.csproj -c Release`
Expected: `빌드했습니다.` — 경고 0, 오류 0

- [ ] **Step 5: Commit**

```bash
git add tools/spritegen/flashgen.py src/PetApp/assets/flash.png src/PetApp/PetApp.csproj
git commit -m "feat: add the level-up ring effect sprite"
```

---

### Task 9: 창 배선

**Files:**
- Modify: `src/PetApp/PetWindow.xaml` (폭 64 -> 112, Canvas 로 교체)
- Modify: `src/PetApp/PetWindow.xaml.cs`

**Interfaces:**
- Consumes: `PlateRenderer.Render`, `PlateRenderer.PlateWidthFor`, `PlateRenderer.GapPx`, `PlateRenderer.PlateHeight` (Task 7); `assets/flash.png` (Task 8)
- Produces: `public void PetWindow.SetLevel(int level, bool leveledUp)`

- [ ] **Step 1: XAML 을 Canvas 로 바꾸고 두 레이어를 추가**

`src/PetApp/PetWindow.xaml` 전체를 아래로 바꾼다:

```xml
<Window x:Class="PetApp.PetWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        WindowStyle="None"
        AllowsTransparency="True"
        Background="Transparent"
        Topmost="True"
        ShowInTaskbar="False"
        ShowActivated="False"
        ResizeMode="NoResize"
        Width="112" Height="64">
    <!-- 창이 64 에서 112 로 넓어졌다. 오른쪽 64x64 가 펫이고, 왼쪽 48px 이 명패 자리다.
         48 은 필요한 최소치에서 올림한 값이다: 4자리 명패 19px x2 = 38, 간격 6px x2 = 12,
         몸의 왼쪽 여백 4px x2 = 8 -> 38 + 12 - 8 = 42.
         창이 넓어지면 Bounce/clamp 가 쓰는 Width 도 커진다. 펫은 오른쪽 끝까지는 그대로
         가지만(창 안에서 오른쪽에 붙어 있으므로), 왼쪽으로는 48px 못 간다 - 그 자리가
         명패이기 때문이고, 명패가 화면 밖으로 나가지 않게 하려면 필요한 동작이다. -->
    <Canvas>
        <Image x:Name="Plate"
               RenderOptions.BitmapScalingMode="NearestNeighbor"
               SnapsToDevicePixels="True" />
        <Image x:Name="Sprite"
               Canvas.Left="48" Width="64" Height="64"
               RenderOptions.BitmapScalingMode="NearestNeighbor"
               SnapsToDevicePixels="True" />
        <!-- 이펙트는 상태 스프라이트 위에 얹힌다. 상태 애니메이션과 별개의 타임라인이다. -->
        <Image x:Name="Flash"
               Canvas.Left="48" Width="64" Height="64"
               Visibility="Collapsed"
               RenderOptions.BitmapScalingMode="NearestNeighbor"
               SnapsToDevicePixels="True" />
    </Canvas>
</Window>
```

- [ ] **Step 2: 코드비하인드에 명패와 이펙트를 붙인다**

`src/PetApp/PetWindow.xaml.cs` 의 `using` 아래, `PetWindow` 클래스 안에 추가한다.

먼저 필드를 기존 필드 선언 뒤(`private IntPtr _hwnd;` 다음 줄)에 넣는다:

```csharp
    // --- 레벨 표시 ---
    private const double PixelScale = 2.0;          // 스프라이트 1px = 화면 2px
    private const int PetCellOriginX = 48;          // Canvas 안에서 펫이 시작하는 x (화면 px)
    private const int PetBodyLeftPx = 4;            // 스프라이트 좌표계에서 몸의 왼쪽 첫 픽셀

    private const int FlashFrames = 8;
    private static readonly Uri FlashUri = new("pack://application:,,,/assets/flash.png", UriKind.Absolute);

    private int _level;
    private BitmapSource[]? _flashFrames;
    private int _flashFrame = -1;                   // -1 = 재생 중 아님
```

`OnSourceInitialized` 의 끝(`Top = work.Bottom - Height;` 다음)에 추가한다:

```csharp
        // 이펙트 프레임을 미리 잘라 얼려 둔다. 재생 중에 자르면 12fps 루프에서 할당이 생긴다.
        _flashFrames = LoadFlashFrames();
```

클래스 끝(마지막 `}` 직전)에 메서드를 추가한다:

```csharp
    /// <summary>
    /// 레벨을 갱신한다. leveledUp 이면 이펙트를 한 번 재생한다.
    /// PetHost 가 30초 주기로 부른다 — 렌더 스레드에서 파일을 읽지 않기 위해서다.
    /// </summary>
    public void SetLevel(int level, bool leveledUp)
    {
        if (level != _level)
        {
            _level = level;
            Plate.Source = PlateRenderer.Render(level);

            // 명패는 펫 왼쪽에, 간격 GapPx 를 두고 붙는다. 몸의 왼쪽 첫 픽셀이 셀 안에서
            // PetBodyLeftPx 이므로 그만큼 더해서 판의 오른쪽 끝을 잡는다.
            var plateWidthPx = PlateRenderer.PlateWidthFor(level) * PixelScale;
            var petBodyLeftPx = PetCellOriginX + PetBodyLeftPx * PixelScale;
            Canvas.SetLeft(Plate, petBodyLeftPx - PlateRenderer.GapPx * PixelScale - plateWidthPx);

            Plate.Width = plateWidthPx;
            Plate.Height = PlateRenderer.PlateHeight * PixelScale;
            // 판의 세로 중심을 몸통 눈높이에 맞춘다 (스프라이트 y 15..23 구간).
            Canvas.SetTop(Plate, 15 * PixelScale);
        }

        if (leveledUp && _flashFrames is not null)
            _flashFrame = 0;
    }

    private static BitmapSource[]? LoadFlashFrames()
    {
        // 리소스가 없거나 크기가 다르면 이펙트만 포기한다. 펫은 계속 돈다.
        try
        {
            var sheet = new BitmapImage();
            sheet.BeginInit();
            sheet.CacheOption = BitmapCacheOption.OnLoad;
            sheet.UriSource = FlashUri;
            sheet.EndInit();
            sheet.Freeze();

            if (sheet.PixelHeight != SpriteSheet.FrameSize
                || sheet.PixelWidth != SpriteSheet.FrameSize * FlashFrames)
                return null;

            var frames = new BitmapSource[FlashFrames];
            for (var i = 0; i < FlashFrames; i++)
            {
                var crop = new CroppedBitmap(sheet,
                    new Int32Rect(i * SpriteSheet.FrameSize, 0,
                                  SpriteSheet.FrameSize, SpriteSheet.FrameSize));
                crop.Freeze();
                frames[i] = crop;
            }
            return frames;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>이펙트를 한 프레임 진행한다. Tick 에서 부른다.</summary>
    private void AdvanceFlash()
    {
        if (_flashFrame < 0 || _flashFrames is null)
        {
            if (Flash.Visibility != Visibility.Collapsed) Flash.Visibility = Visibility.Collapsed;
            return;
        }

        Flash.Source = _flashFrames[_flashFrame];
        if (Flash.Visibility != Visibility.Visible) Flash.Visibility = Visibility.Visible;

        _flashFrame++;
        if (_flashFrame >= FlashFrames)
        {
            _flashFrame = -1;
            Flash.Visibility = Visibility.Collapsed;
        }
    }
```

`Tick()` 안에서 이펙트를 진행시킨다. **잠듦 조기 반환보다 앞에 둔다** — 잠든 채로 레벨이
올라도 이펙트는 보여야 하기 때문이다. `_isFullscreenHiding` 처리 블록 바로 다음,
`_idleTicks` 계산 바로 앞에 한 줄 넣는다:

```csharp
        // 레벨업 이펙트는 상태와 무관한 자기 타임라인으로 돈다. 잠듦 반환보다 앞에 있어야
        // 잠든 채로 레벨이 올라도 재생된다.
        AdvanceFlash();
```

또한 `using System.Windows.Controls;` 와 `using System.Windows.Media.Imaging;` 를
파일 맨 위 `using` 목록에 추가한다 (`Canvas`, `BitmapSource`, `CroppedBitmap`, `Int32Rect` 용).

- [ ] **Step 3: 빌드**

Run: `dotnet build src/PetApp/PetApp.csproj -c Release`
Expected: `빌드했습니다.` — 경고 0, 오류 0

- [ ] **Step 4: 눈으로 확인**

```bash
cd "c:/Users/hayoul1999.YOUL-HOUSE/Desktop/github/claude-pet"
dotnet publish src/PetApp/PetApp.csproj -c Release -r win-x64 \
  -p:SelfContained=false -p:PublishSingleFile=true \
  -p:DebugType=none -p:DebugSymbols=false -o /tmp/pet-verify
```

그다음 `/tmp/pet-verify/pet.exe` 를 띄우고 명패가 보이는지 확인한다. plugin/bin 은 건드리지 않는다. 세션 파일이 없으면 워치독이 10초 뒤 닫으므로,
살아 있는 프로세스를 가리키는 세션 레코드를 먼저 넣는다.

Expected: 화면 하단에 펫이 서 있고 그 **왼쪽**에 검은 판 + 흰 숫자가 보인다. 판과 펫 사이가
붙지 않고 조금 떨어져 있다.

- [ ] **Step 5: Commit**

```bash
git add src/PetApp/PetWindow.xaml src/PetApp/PetWindow.xaml.cs
git commit -m "feat: show the level plate beside the pet and play the level-up ring"
```

---

### Task 10: 호스트 배선

**Files:**
- Modify: `src/PetApp/PetHost.cs`

**Interfaces:**
- Consumes: `UsageTracker`, `UsageStore`, `TranscriptCostScanner` (Tasks 4~6); `PetWindow.SetLevel` (Task 9)
- Produces: 없음 (최종 조립)

- [ ] **Step 1: UsageTracker 를 30초 주기로 붙인다**

`src/PetApp/PetHost.cs` 를 세 군데 고친다.

**(1)** `StaleAfterMs` 선언 다음에 상수와 필드를 추가한다:

```csharp
    /// <summary>
    /// 레벨 갱신 주기. 1Hz 폴링에 얹지 않는 이유는 매초 파일을 재스캔할 이유가 없기 때문이다.
    /// 레벨 하나가 오르는 데 가장 빠른 사용자도 3일이 걸리므로 30초 지연은 무의미하다 (스펙 §7.4).
    /// </summary>
    private const int LevelPollTicks = 30;

    private readonly UsageTracker _usage;
    private int _levelTickCounter;
```

**(2)** 생성자에서 `_usage` 를 만든다. `_watchdog = ...` 다음 줄에 넣는다:

```csharp
        // 트랜스크립트는 데이터 디렉터리가 아니라 Claude Code 의 프로젝트 디렉터리에 있다.
        var projectsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "projects");
        _usage = new UsageTracker(projectsRoot, new UsageStore(dataDir), new TranscriptCostScanner());
```

**(3)** `PollCore()` 의 마지막 줄(`_window.SetState(...)`) 다음에 레벨 갱신을 넣는다:

```csharp
        // 30초에 한 번만 레벨을 다시 센다. Refresh 는 절대 던지지 않지만, PollCore 전체가
        // 이미 Poll 의 catch-all 안에 있으므로 여기서 다시 감쌀 필요는 없다.
        if (_levelTickCounter <= 0)
        {
            var snapshot = _usage.Refresh();
            _window.SetLevel(snapshot.Level, snapshot.LeveledUp);
            _levelTickCounter = LevelPollTicks;
        }
        _levelTickCounter--;
```

- [ ] **Step 2: 빌드와 전체 테스트**

Run: `dotnet build claude-pet.sln -c Release && dotnet test tests/PetCore.Tests/PetCore.Tests.csproj`
Expected: 빌드 경고 0 · 오류 0, 테스트 `통과!` 실패 0 · 통과 139

- [ ] **Step 3: 실제 데이터로 끝에서 끝까지 확인**

```bash
cd "c:/Users/hayoul1999.YOUL-HOUSE/Desktop/github/claude-pet"
dotnet publish src/PetApp/PetApp.csproj -c Release -r win-x64 \
  -p:SelfContained=false -p:PublishSingleFile=true \
  -p:DebugType=none -p:DebugSymbols=false -o /tmp/pet-verify
```

`/tmp/pet-verify/pet.exe` 를 띄운 뒤 상태 파일을 확인한다:

```bash
cat ~/.claude/plugins/data/claude-pet-claude-pet-local/usage.json | python -m json.tool | head -20
```

Expected: `totalCostUsd` 가 6,000 언저리, `level` 이 99 언저리, `files` 에 수백 개 항목.
명패에 그 숫자가 떠 있어야 한다.

두 번째 기동이 빨라졌는지 확인한다 — 두 번째 `Refresh` 는 바뀐 파일 하나만 읽어야 한다.

- [ ] **Step 4: Commit**

```bash
git add src/PetApp/PetHost.cs
git commit -m "feat: refresh the level every 30 seconds off the render path"
```

---

## Self-Review

**스펙 커버리지**

| 스펙 절 | 태스크 |
|---|---|
| §2 비용 지표, 단가표, 폴백 | Task 1 |
| §2.4 금액 비노출 | Task 9 (명패는 레벨만 그린다) |
| §3.1 데이터 출처 | Task 6 (`~/.claude/projects` 재귀 탐색), Task 10 (경로 조립) |
| §3.2~3.3 중복 제거, 파일 단위 폐기 | Task 4 |
| §4 레벨 곡선, 유도 상수 | Task 3 |
| §5.1~5.4 명패 색·위치·간격·가변 폭 | Task 7, Task 9 |
| §6 레벨업 이펙트 | Task 8, Task 9 |
| §7.2 상태 파일 | Task 5 |
| §7.2 캐시 판단, 전량 재스캔 | Task 6 |
| §7.4 30초 주기 | Task 10 |
| §8 렌더 스레드 비차단 | Task 10 (1Hz 타이머의 30틱마다, 12fps 루프 밖) |

**타입 일관성 확인**

- `TokenCounts` — Task 1 정의, Task 2·4 사용. 동일
- `UsageRecord` — Task 2 정의, Task 4 사용. 동일
- `TranscriptCostScanner.ScanFile` — Task 4 에서 `virtual` 로 선언, Task 6 테스트가 `override`. 일치
- `UsageSnapshot(TotalCostUsd, Level, LeveledUp)` — Task 6 정의, Task 10 사용. 동일
- `PetWindow.SetLevel(int, bool)` — Task 9 정의, Task 10 호출. 동일
- `SpriteSheet.FrameSize` — 기존 코드의 `public const int FrameSize = 32`. Task 9 에서 사용
- `PlateRenderer.GapPx` / `PlateWidthFor` / `PlateHeight` / `Render` — Task 7 정의, Task 9 사용. 동일

**남은 위험**

- Task 9 의 창 폭 변경(64 → 112)은 `Bounce` 와 clamp 가 쓰는 `Width` 를 바꾼다. `_x` 는
  **창의** 왼쪽 좌표이고 펫은 창 안에서 오른쪽에 붙어 있으므로:
  - 오른쪽 — `_x` 최대가 `work.Right - 112` 이고 펫은 창의 x 48..112 를 차지하니 펫의
    오른쪽 끝이 정확히 `work.Right` 에 닿는다. **이전과 동일하다.**
  - 왼쪽 — `_x` 최소가 `work.Left` 이므로 펫의 왼쪽은 `work.Left + 48` 까지만 간다.
    **48px 못 간다.** 그 자리가 명패이고, 명패가 화면 밖으로 나가지 않게 하려면 필요하다.

  Task 9 Step 4 에서 좌우 양 끝까지 걸어가 확인한다: 오른쪽은 화면 끝에 닿아야 하고,
  왼쪽은 명패가 잘리지 않은 채로 멈춰야 한다.
