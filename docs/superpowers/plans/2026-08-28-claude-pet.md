# claude-pet Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Claude Code 세션에 연동되어 등장·소멸하는 Windows 데스크톱 펫을 만들되, 코딩 작업을 전혀 방해하지 않는다.

**Architecture:** 세 조각으로 나뉜다. `PetCore`는 GUI를 모르는 순수 로직(파서·레지스트리·워치독·상태 머신)이며 전부 단위 테스트한다. `PetApp`은 WPF 투명 최상위 창으로 `PetCore`의 산출물을 그리기만 한다. 플러그인은 훅 3개와 `pet.exe`만 담는 껍데기다. 도구 호출 핫패스에는 훅이 하나도 없으며, 도구 단위 정보는 펫이 트랜스크립트 JSONL을 읽기 전용으로 tailing해서 얻는다.

**Tech Stack:** .NET 10 (`net10.0-windows`), WPF, xUnit, Python 3 (빌드 타임 스프라이트 생성 전용), PowerShell (훅 스크립트).

## Global Constraints

스펙의 프로젝트 전역 요구사항. **모든 태스크의 요구사항에 암묵적으로 포함된다.**

- **방해 0이 최상위 제약이다.** 다른 모든 요구사항보다 우선하며, 기능이 이와 충돌하면 기능을 버린다.
- 대상 프레임워크는 `net10.0-windows`. 라이브러리 프로젝트도 동일.
- **트랜스크립트 파일은 반드시 `FileAccess.Read` + `FileShare.ReadWrite | FileShare.Delete`로 연다.** 배타적 잠금 금지.
- **훅 스크립트는 어떤 입력에도 exit 0으로 끝난다.** 오류를 삼킨다.
- 훅은 `SessionStart`, `Notification`, `SessionEnd` 세 개뿐이며 전부 `"async": true`. `PostToolUse`, `UserPromptSubmit`, `Stop` 훅은 절대 추가하지 않는다.
- 렌더링은 12 fps 고정. 유휴·가림 상태에서는 렌더링 정지.
- 프로세스 우선순위는 `BelowNormal`.
- 펫의 활동 영역은 주 모니터 작업 영역 하단 15% 이내 가로 띠.
- 창은 `WS_EX_NOACTIVATE`, `WS_EX_TRANSPARENT`, `WS_EX_TOOLWINDOW`를 모두 가진다.
- 펫은 프로세스 전체에서 한 마리(named mutex 싱글턴).
- 경로 참조는 `${CLAUDE_PLUGIN_ROOT}`, 상태 저장은 `${CLAUDE_PLUGIN_DATA}`.

## File Structure

```
claude-pet/
├── claude-pet.sln
├── src/
│   ├── PetCore/                        # GUI를 모르는 순수 로직. 전부 테스트한다.
│   │   ├── PetCore.csproj
│   │   ├── TranscriptEvent.cs          # 파싱 결과 이벤트 모델
│   │   ├── TranscriptParser.cs         # JSONL 한 줄 → TranscriptEvent 목록
│   │   ├── TranscriptTail.cs           # 잠금 없는 증분 읽기
│   │   ├── SessionRecord.cs            # 세션 등록 레코드 모델
│   │   ├── SessionRegistry.cs          # 세션 디렉터리 읽기
│   │   ├── IProcessProbe.cs            # PID 생존 확인 추상화 (테스트 이음새)
│   │   ├── WindowsProcessProbe.cs      # 실제 구현
│   │   ├── Watchdog.cs                 # 종료 판정
│   │   ├── PetState.cs                 # 상태 enum
│   │   ├── PetStateMachine.cs          # 이벤트 → 상태
│   │   └── SingleInstance.cs           # named mutex
│   └── PetApp/                         # WPF. 그리기만 한다.
│       ├── PetApp.csproj
│       ├── App.xaml / App.xaml.cs
│       ├── PetWindow.xaml / .cs
│       ├── NativeMethods.cs            # WS_EX_* 및 전체화면 판정 interop
│       ├── SpriteSheet.cs              # 시트 로딩 + 프레임 슬라이싱
│       └── assets/                     # spritegen 산출물 (커밋함)
├── tests/
│   └── PetCore.Tests/
│       ├── PetCore.Tests.csproj
│       ├── TranscriptParserTests.cs
│       ├── TranscriptTailTests.cs
│       ├── SessionRegistryTests.cs
│       ├── WatchdogTests.cs
│       └── PetStateMachineTests.cs
├── tools/spritegen/spritegen.py        # 빌드 타임 전용
├── plugin/
│   ├── .claude-plugin/plugin.json
│   └── hooks/
│       ├── hooks.json
│       ├── session_start.ps1
│       ├── notification.ps1
│       └── session_end.ps1
└── bench/measure_overhead.ps1          # 방해 0 측정
```

---

### Task 1: 솔루션 스캐폴드와 첫 테스트

프로젝트 뼈대를 세우고 `dotnet test`가 초록으로 도는 것까지 확인한다.

**Files:**
- Create: `claude-pet.sln`
- Create: `src/PetCore/PetCore.csproj`
- Create: `src/PetCore/PetState.cs`
- Create: `tests/PetCore.Tests/PetCore.Tests.csproj`
- Test: `tests/PetCore.Tests/PetStateTests.cs`

**Interfaces:**
- Consumes: 없음 (첫 태스크)
- Produces: `PetCore.PetState` — `enum { Idle, Reading, Writing, Running, Error, NeedsYou }`

- [ ] **Step 1: 솔루션과 프로젝트 생성**

```bash
cd claude-pet
dotnet new sln -n claude-pet
dotnet new classlib -o src/PetCore -f net10.0
dotnet new xunit -o tests/PetCore.Tests -f net10.0
dotnet sln add src/PetCore/PetCore.csproj tests/PetCore.Tests/PetCore.Tests.csproj
dotnet add tests/PetCore.Tests/PetCore.Tests.csproj reference src/PetCore/PetCore.csproj
rm -f src/PetCore/Class1.cs tests/PetCore.Tests/UnitTest1.cs
```

- [ ] **Step 2: 실패하는 테스트 작성**

`tests/PetCore.Tests/PetStateTests.cs`:

```csharp
using PetCore;
using Xunit;

public class PetStateTests
{
    [Fact]
    public void PetState_HasAllSixStates()
    {
        Assert.Equal(6, Enum.GetValues<PetState>().Length);
        Assert.True(Enum.IsDefined(PetState.NeedsYou));
    }
}
```

- [ ] **Step 3: 테스트가 실패하는지 확인**

Run: `dotnet test --filter PetState_HasAllSixStates`
Expected: FAIL — `PetState` 형식을 찾을 수 없음 (CS0246)

- [ ] **Step 4: 최소 구현**

`src/PetCore/PetState.cs`:

```csharp
namespace PetCore;

public enum PetState
{
    Idle,
    Reading,
    Writing,
    Running,
    Error,
    NeedsYou
}
```

- [ ] **Step 5: 테스트 통과 확인**

Run: `dotnet test --filter PetState_HasAllSixStates`
Expected: PASS — Passed! - Failed: 0, Passed: 1

- [ ] **Step 6: 커밋**

```bash
git add claude-pet.sln src/PetCore tests/PetCore.Tests
git commit -m "feat: scaffold PetCore solution with PetState"
```

---

### Task 2: 트랜스크립트 파서

JSONL 한 줄을 이벤트로 변환한다. `message.content`가 문자열일 수도 배열일 수도 있다는 점이 함정이다.

**Files:**
- Create: `src/PetCore/TranscriptEvent.cs`
- Create: `src/PetCore/TranscriptParser.cs`
- Test: `tests/PetCore.Tests/TranscriptParserTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces:
  - `PetCore.TranscriptEventKind` — `enum { ToolUse, ToolResult, AssistantText, Thinking, Other }`
  - `PetCore.TranscriptEvent` — `record(TranscriptEventKind Kind, string? ToolName, bool IsError)`
  - `PetCore.TranscriptParser.ParseLine(string line) -> IReadOnlyList<TranscriptEvent>` (파싱 불가 시 빈 목록)

- [ ] **Step 1: 실패하는 테스트 작성**

`tests/PetCore.Tests/TranscriptParserTests.cs`:

```csharp
using PetCore;
using Xunit;

public class TranscriptParserTests
{
    [Fact]
    public void ParsesToolUse_WithToolName()
    {
        var line = """
        {"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Read","input":{}}]}}
        """;

        var events = TranscriptParser.ParseLine(line);

        var e = Assert.Single(events);
        Assert.Equal(TranscriptEventKind.ToolUse, e.Kind);
        Assert.Equal("Read", e.ToolName);
    }

    [Fact]
    public void ParsesToolResult_WithErrorFlag()
    {
        var line = """
        {"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","is_error":true}]}}
        """;

        var e = Assert.Single(TranscriptParser.ParseLine(line));
        Assert.Equal(TranscriptEventKind.ToolResult, e.Kind);
        Assert.True(e.IsError);
    }

    [Fact]
    public void ParsesAssistantText_WhenContentIsPlainString()
    {
        var line = """
        {"type":"assistant","message":{"role":"assistant","content":"done"}}
        """;

        var e = Assert.Single(TranscriptParser.ParseLine(line));
        Assert.Equal(TranscriptEventKind.AssistantText, e.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{not json")]
    [InlineData("{\"type\":\"summary\",\"summary\":\"x\"}")]
    public void ReturnsEmpty_ForUnparseableOrIrrelevantLines(string line)
    {
        Assert.Empty(TranscriptParser.ParseLine(line));
    }
}
```

- [ ] **Step 2: 테스트가 실패하는지 확인**

Run: `dotnet test --filter TranscriptParserTests`
Expected: FAIL — `TranscriptParser` 형식을 찾을 수 없음 (CS0246)

- [ ] **Step 3: 최소 구현**

`src/PetCore/TranscriptEvent.cs`:

```csharp
namespace PetCore;

public enum TranscriptEventKind
{
    ToolUse,
    ToolResult,
    AssistantText,
    Thinking,
    Other
}

public sealed record TranscriptEvent(
    TranscriptEventKind Kind,
    string? ToolName = null,
    bool IsError = false);
```

`src/PetCore/TranscriptParser.cs`:

```csharp
using System.Text.Json;

namespace PetCore;

public static class TranscriptParser
{
    public static IReadOnlyList<TranscriptEvent> ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return Array.Empty<TranscriptEvent>();

        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) { return Array.Empty<TranscriptEvent>(); }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("message", out var message))
                return Array.Empty<TranscriptEvent>();
            if (!message.TryGetProperty("content", out var content))
                return Array.Empty<TranscriptEvent>();

            if (content.ValueKind == JsonValueKind.String)
                return new[] { new TranscriptEvent(TranscriptEventKind.AssistantText) };

            if (content.ValueKind != JsonValueKind.Array)
                return Array.Empty<TranscriptEvent>();

            var results = new List<TranscriptEvent>();
            foreach (var item in content.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out var typeProp))
                    continue;

                switch (typeProp.GetString())
                {
                    case "tool_use":
                        var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                        results.Add(new TranscriptEvent(TranscriptEventKind.ToolUse, name));
                        break;

                    case "tool_result":
                        var isError = item.TryGetProperty("is_error", out var e)
                                      && e.ValueKind == JsonValueKind.True;
                        results.Add(new TranscriptEvent(
                            TranscriptEventKind.ToolResult, null, isError));
                        break;

                    case "text":
                        results.Add(new TranscriptEvent(TranscriptEventKind.AssistantText));
                        break;

                    case "thinking":
                        results.Add(new TranscriptEvent(TranscriptEventKind.Thinking));
                        break;
                }
            }
            return results;
        }
    }
}
```

- [ ] **Step 4: 테스트 통과 확인**

Run: `dotnet test --filter TranscriptParserTests`
Expected: PASS — Failed: 0, Passed: 7

- [ ] **Step 5: 커밋**

```bash
git add src/PetCore/TranscriptEvent.cs src/PetCore/TranscriptParser.cs tests/PetCore.Tests/TranscriptParserTests.cs
git commit -m "feat: parse transcript JSONL into events"
```

---

### Task 3: 잠금 없는 트랜스크립트 tailing

**이 태스크가 방해 0의 핵심이다.** 펫이 파일을 배타적으로 열면 Claude Code의 쓰기가 실패한다. 그 실패를 재현하는 테스트를 먼저 쓴다.

**Files:**
- Create: `src/PetCore/TranscriptTail.cs`
- Test: `tests/PetCore.Tests/TranscriptTailTests.cs`

**Interfaces:**
- Consumes: `TranscriptParser.ParseLine`
- Produces:
  - `PetCore.TranscriptTail(string path)` — 생성자
  - `TranscriptTail.ReadNew() -> IReadOnlyList<TranscriptEvent>` — 마지막 위치 이후의 새 줄만 파싱
  - `TranscriptTail.Position` — `long`, 현재까지 읽은 바이트 오프셋

- [ ] **Step 1: 실패하는 테스트 작성**

`tests/PetCore.Tests/TranscriptTailTests.cs`:

```csharp
using PetCore;
using Xunit;

public class TranscriptTailTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tail-{Guid.NewGuid():N}.jsonl");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private const string ToolUseLine =
        """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t","name":"Bash","input":{}}]}}""";

    [Fact]
    public void ReadNew_ReturnsOnlyLinesAppendedSinceLastCall()
    {
        File.WriteAllText(_path, ToolUseLine + "\n");
        var tail = new TranscriptTail(_path);

        Assert.Single(tail.ReadNew());
        Assert.Empty(tail.ReadNew());

        File.AppendAllText(_path, ToolUseLine + "\n");
        Assert.Single(tail.ReadNew());
    }

    [Fact]
    public void ReadNew_DoesNotBlockConcurrentWriter()
    {
        // 방해 0 검증: 펫이 읽는 동안 Claude Code가 같은 파일에 쓸 수 있어야 한다.
        File.WriteAllText(_path, ToolUseLine + "\n");
        var tail = new TranscriptTail(_path);
        tail.ReadNew();

        using var writer = new FileStream(
            _path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var bytes = System.Text.Encoding.UTF8.GetBytes(ToolUseLine + "\n");

        var ex = Record.Exception(() =>
        {
            writer.Write(bytes, 0, bytes.Length);
            writer.Flush();
            tail.ReadNew();
        });

        Assert.Null(ex);
    }

    [Fact]
    public void ReadNew_ReturnsEmpty_WhenFileMissing()
    {
        var tail = new TranscriptTail(Path.Combine(Path.GetTempPath(), "does-not-exist.jsonl"));
        Assert.Empty(tail.ReadNew());
    }

    [Fact]
    public void ReadNew_RestartsFromZero_WhenFileTruncated()
    {
        File.WriteAllText(_path, ToolUseLine + "\n" + ToolUseLine + "\n");
        var tail = new TranscriptTail(_path);
        Assert.Equal(2, tail.ReadNew().Count);

        File.WriteAllText(_path, ToolUseLine + "\n");
        Assert.Single(tail.ReadNew());
    }
}
```

- [ ] **Step 2: 테스트가 실패하는지 확인**

Run: `dotnet test --filter TranscriptTailTests`
Expected: FAIL — `TranscriptTail` 형식을 찾을 수 없음 (CS0246)

- [ ] **Step 3: 최소 구현**

`src/PetCore/TranscriptTail.cs`:

```csharp
using System.Text;

namespace PetCore;

/// <summary>
/// 트랜스크립트 JSONL을 증분 읽기한다.
/// 파일에 어떤 잠금도 걸지 않는다 — Claude Code의 쓰기를 절대 방해해서는 안 된다.
/// </summary>
public sealed class TranscriptTail
{
    private readonly string _path;

    public TranscriptTail(string path) => _path = path;

    public long Position { get; private set; }

    public IReadOnlyList<TranscriptEvent> ReadNew()
    {
        if (!File.Exists(_path))
            return Array.Empty<TranscriptEvent>();

        try
        {
            using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            if (stream.Length < Position)
                Position = 0;   // 파일이 잘렸다 (컴팩션 등) — 처음부터 다시.

            stream.Seek(Position, SeekOrigin.Begin);

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var events = new List<TranscriptEvent>();
            string? line;
            while ((line = reader.ReadLine()) is not null)
                events.AddRange(TranscriptParser.ParseLine(line));

            Position = stream.Length;
            return events;
        }
        catch (IOException)
        {
            // 일시적 경합. 다음 주기에 다시 읽는다. 절대 던지지 않는다.
            return Array.Empty<TranscriptEvent>();
        }
    }
}
```

- [ ] **Step 4: 테스트 통과 확인**

Run: `dotnet test --filter TranscriptTailTests`
Expected: PASS — Failed: 0, Passed: 4

- [ ] **Step 5: 커밋**

```bash
git add src/PetCore/TranscriptTail.cs tests/PetCore.Tests/TranscriptTailTests.cs
git commit -m "feat: lock-free transcript tailing that never blocks the writer"
```

---

### Task 4: 세션 레지스트리

훅이 남긴 세션 JSON 파일을 읽는다. 훅 스크립트가 쓰는 형식과 여기서 읽는 형식이 계약이다.

**Files:**
- Create: `src/PetCore/SessionRecord.cs`
- Create: `src/PetCore/SessionRegistry.cs`
- Test: `tests/PetCore.Tests/SessionRegistryTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces:
  - `PetCore.SessionRecord` — `record(string SessionId, string TranscriptPath, int Pid, long PidStartUnixMs, long TouchedUnixMs)`
  - `PetCore.SessionRegistry(string directory)` — 생성자
  - `SessionRegistry.ReadAll() -> IReadOnlyList<SessionRecord>` — 손상된 파일은 조용히 건너뛴다

- [ ] **Step 1: 실패하는 테스트 작성**

`tests/PetCore.Tests/SessionRegistryTests.cs`:

```csharp
using PetCore;
using Xunit;

public class SessionRegistryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sessions-{Guid.NewGuid():N}");

    public SessionRegistryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private void WriteSession(string id, string json) =>
        File.WriteAllText(Path.Combine(_dir, $"{id}.json"), json);

    [Fact]
    public void ReadAll_ParsesWellFormedRecords()
    {
        WriteSession("abc", """
        {"sessionId":"abc","transcriptPath":"C:\\t\\abc.jsonl","pid":1234,"pidStartUnixMs":111,"touchedUnixMs":222}
        """);

        var r = Assert.Single(new SessionRegistry(_dir).ReadAll());
        Assert.Equal("abc", r.SessionId);
        Assert.Equal(1234, r.Pid);
        Assert.Equal(111, r.PidStartUnixMs);
    }

    [Fact]
    public void ReadAll_SkipsCorruptFilesInsteadOfThrowing()
    {
        WriteSession("good", """
        {"sessionId":"good","transcriptPath":"p","pid":1,"pidStartUnixMs":0,"touchedUnixMs":0}
        """);
        WriteSession("bad", "{ this is not json");

        var records = new SessionRegistry(_dir).ReadAll();

        Assert.Single(records);
        Assert.Equal("good", records[0].SessionId);
    }

    [Fact]
    public void ReadAll_ReturnsEmpty_WhenDirectoryMissing()
    {
        var missing = Path.Combine(_dir, "nope");
        Assert.Empty(new SessionRegistry(missing).ReadAll());
    }
}
```

- [ ] **Step 2: 테스트가 실패하는지 확인**

Run: `dotnet test --filter SessionRegistryTests`
Expected: FAIL — `SessionRegistry` 형식을 찾을 수 없음 (CS0246)

- [ ] **Step 3: 최소 구현**

`src/PetCore/SessionRecord.cs`:

```csharp
namespace PetCore;

public sealed record SessionRecord(
    string SessionId,
    string TranscriptPath,
    int Pid,
    long PidStartUnixMs,
    long TouchedUnixMs);
```

`src/PetCore/SessionRegistry.cs`:

```csharp
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
```

- [ ] **Step 4: 테스트 통과 확인**

Run: `dotnet test --filter SessionRegistryTests`
Expected: PASS — Failed: 0, Passed: 3

- [ ] **Step 5: 커밋**

```bash
git add src/PetCore/SessionRecord.cs src/PetCore/SessionRegistry.cs tests/PetCore.Tests/SessionRegistryTests.cs
git commit -m "feat: read session registry written by hooks"
```

---

### Task 5: 워치독

`SessionEnd`를 믿지 않는다. PID 생존이 권위다. PID 재사용을 막기 위해 시작 시각까지 대조한다.

**Files:**
- Create: `src/PetCore/IProcessProbe.cs`
- Create: `src/PetCore/WindowsProcessProbe.cs`
- Create: `src/PetCore/Watchdog.cs`
- Test: `tests/PetCore.Tests/WatchdogTests.cs`

**Interfaces:**
- Consumes: `SessionRecord`, `SessionRegistry`
- Produces:
  - `PetCore.IProcessProbe.IsAlive(int pid, long startUnixMs) -> bool`
  - `PetCore.WindowsProcessProbe : IProcessProbe`
  - `PetCore.Watchdog(IProcessProbe probe, TimeSpan grace)` — 생성자
  - `Watchdog.ShouldExit(IReadOnlyList<SessionRecord> sessions, DateTimeOffset now) -> bool`

- [ ] **Step 1: 실패하는 테스트 작성**

`tests/PetCore.Tests/WatchdogTests.cs`:

```csharp
using PetCore;
using Xunit;

public class WatchdogTests
{
    private sealed class FakeProbe : IProcessProbe
    {
        public HashSet<int> Alive { get; } = new();
        public bool IsAlive(int pid, long startUnixMs) => Alive.Contains(pid);
    }

    private static SessionRecord Session(int pid) =>
        new($"s{pid}", "p", pid, 0, 0);

    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(10);

    [Fact]
    public void DoesNotExit_WhileAnySessionIsAlive()
    {
        var probe = new FakeProbe();
        probe.Alive.Add(1);
        var watchdog = new Watchdog(probe, Grace);

        Assert.False(watchdog.ShouldExit(new[] { Session(1), Session(2) }, T0));
    }

    [Fact]
    public void DoesNotExitImmediately_WhenAllSessionsDie()
    {
        // /clear 와 /compact 는 SessionEnd 직후 SessionStart 를 낸다.
        // 유예 시간이 없으면 펫이 깜빡인다.
        var watchdog = new Watchdog(new FakeProbe(), Grace);

        Assert.False(watchdog.ShouldExit(new[] { Session(1) }, T0));
    }

    [Fact]
    public void Exits_AfterGraceElapsesWithNoLiveSession()
    {
        var watchdog = new Watchdog(new FakeProbe(), Grace);

        Assert.False(watchdog.ShouldExit(new[] { Session(1) }, T0));
        Assert.True(watchdog.ShouldExit(new[] { Session(1) }, T0 + Grace + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void GraceResets_WhenASessionComesBackAlive()
    {
        var probe = new FakeProbe();
        var watchdog = new Watchdog(probe, Grace);

        watchdog.ShouldExit(new[] { Session(1) }, T0);          // 유예 시작
        probe.Alive.Add(1);                                      // 세션 부활 (/clear 후 재등록)
        watchdog.ShouldExit(new[] { Session(1) }, T0 + TimeSpan.FromSeconds(5));
        probe.Alive.Remove(1);

        var justAfterOriginalGrace = T0 + Grace + TimeSpan.FromSeconds(1);
        Assert.False(watchdog.ShouldExit(new[] { Session(1) }, justAfterOriginalGrace));
    }

    [Fact]
    public void Exits_AfterGrace_WhenRegistryIsEmpty()
    {
        var watchdog = new Watchdog(new FakeProbe(), Grace);

        Assert.False(watchdog.ShouldExit(Array.Empty<SessionRecord>(), T0));
        Assert.True(watchdog.ShouldExit(Array.Empty<SessionRecord>(), T0 + Grace + TimeSpan.FromSeconds(1)));
    }
}
```

- [ ] **Step 2: 테스트가 실패하는지 확인**

Run: `dotnet test --filter WatchdogTests`
Expected: FAIL — `IProcessProbe`, `Watchdog` 형식을 찾을 수 없음 (CS0246)

- [ ] **Step 3: 최소 구현**

`src/PetCore/IProcessProbe.cs`:

```csharp
namespace PetCore;

public interface IProcessProbe
{
    /// <summary>
    /// PID 재사용을 막기 위해 시작 시각까지 대조한다.
    /// startUnixMs 가 0 이면 시작 시각 대조를 생략한다.
    /// </summary>
    bool IsAlive(int pid, long startUnixMs);
}
```

`src/PetCore/WindowsProcessProbe.cs`:

```csharp
using System.Diagnostics;

namespace PetCore;

public sealed class WindowsProcessProbe : IProcessProbe
{
    private const long ToleranceMs = 2000;

    public bool IsAlive(int pid, long startUnixMs)
    {
        if (pid <= 0) return false;

        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) return false;
            if (startUnixMs == 0) return true;

            var actual = new DateTimeOffset(process.StartTime.ToUniversalTime())
                .ToUnixTimeMilliseconds();
            return Math.Abs(actual - startUnixMs) <= ToleranceMs;
        }
        catch (ArgumentException)
        {
            return false;   // 해당 PID의 프로세스가 없다.
        }
        catch (InvalidOperationException)
        {
            return false;   // 조회 중 종료됨.
        }
    }
}
```

`src/PetCore/Watchdog.cs`:

```csharp
namespace PetCore;

/// <summary>
/// 펫의 종료 시점을 판정한다.
/// SessionEnd 훅은 신뢰할 수 없으므로(크래시·터미널 강제 종료 시 미발동,
/// /clear·/compact 시 오발동) PID 생존이 권위다.
/// </summary>
public sealed class Watchdog
{
    private readonly IProcessProbe _probe;
    private readonly TimeSpan _grace;
    private DateTimeOffset? _emptySince;

    public Watchdog(IProcessProbe probe, TimeSpan grace)
    {
        _probe = probe;
        _grace = grace;
    }

    public bool ShouldExit(IReadOnlyList<SessionRecord> sessions, DateTimeOffset now)
    {
        var anyAlive = sessions.Any(s => _probe.IsAlive(s.Pid, s.PidStartUnixMs));

        if (anyAlive)
        {
            _emptySince = null;
            return false;
        }

        _emptySince ??= now;
        return now - _emptySince.Value > _grace;
    }
}
```

- [ ] **Step 4: 테스트 통과 확인**

Run: `dotnet test --filter WatchdogTests`
Expected: PASS — Failed: 0, Passed: 5

- [ ] **Step 5: 커밋**

```bash
git add src/PetCore/IProcessProbe.cs src/PetCore/WindowsProcessProbe.cs src/PetCore/Watchdog.cs tests/PetCore.Tests/WatchdogTests.cs
git commit -m "feat: PID-heartbeat watchdog that survives /clear and crashes"
```

---

### Task 6: 상태 머신

이벤트를 상태로 바꾼다. 턴 종료 판정이 핵심이다 — **`tool_use`가 없는 assistant 메시지가 곧 턴 종료다.** 이것은 휴리스틱이 아니라 정확한 규칙이다.

**Files:**
- Create: `src/PetCore/PetStateMachine.cs`
- Test: `tests/PetCore.Tests/PetStateMachineTests.cs`

**Interfaces:**
- Consumes: `TranscriptEvent`, `TranscriptEventKind`, `PetState`
- Produces:
  - `PetCore.NeedsYouLevel` — `enum { None = 0, YourTurn = 1, Blocked = 2, Abandoned = 3 }`
  - `PetCore.PetStateMachine.Current` — `PetState`
  - `PetCore.PetStateMachine.NeedsYou` — `NeedsYouLevel`
  - `PetStateMachine.Apply(TranscriptEvent e) -> void`
  - `PetStateMachine.ApplyNotification(string notificationType) -> void`
  - `PetStateMachine.Sequence` — `long`, 이벤트를 적용할 때마다 증가
  - `PetCore.PetStateMachine.Aggregate(IReadOnlyCollection<PetStateMachine>) -> PetState` (정적)

- [ ] **Step 1: 실패하는 테스트 작성**

`tests/PetCore.Tests/PetStateMachineTests.cs`:

```csharp
using PetCore;
using Xunit;

public class PetStateMachineTests
{
    private static TranscriptEvent Tool(string name) =>
        new(TranscriptEventKind.ToolUse, name);

    [Theory]
    [InlineData("Read", PetState.Reading)]
    [InlineData("Grep", PetState.Reading)]
    [InlineData("Glob", PetState.Reading)]
    [InlineData("WebFetch", PetState.Reading)]
    [InlineData("Edit", PetState.Writing)]
    [InlineData("Write", PetState.Writing)]
    [InlineData("NotebookEdit", PetState.Writing)]
    [InlineData("Bash", PetState.Running)]
    [InlineData("PowerShell", PetState.Running)]
    public void MapsToolNameToState(string tool, PetState expected)
    {
        var machine = new PetStateMachine();
        machine.Apply(Tool(tool));
        Assert.Equal(expected, machine.Current);
    }

    [Fact]
    public void UnknownTool_FallsBackToRunning()
    {
        var machine = new PetStateMachine();
        machine.Apply(Tool("SomeFutureTool"));
        Assert.Equal(PetState.Running, machine.Current);
    }

    [Fact]
    public void ErrorResult_EntersErrorState()
    {
        var machine = new PetStateMachine();
        machine.Apply(Tool("Bash"));
        machine.Apply(new TranscriptEvent(TranscriptEventKind.ToolResult, null, IsError: true));
        Assert.Equal(PetState.Error, machine.Current);
    }

    [Fact]
    public void AssistantTextWithNoPendingTool_MeansTurnEnded()
    {
        var machine = new PetStateMachine();
        machine.Apply(Tool("Read"));
        machine.Apply(new TranscriptEvent(TranscriptEventKind.ToolResult));
        machine.Apply(new TranscriptEvent(TranscriptEventKind.AssistantText));

        Assert.Equal(PetState.NeedsYou, machine.Current);
        Assert.Equal(NeedsYouLevel.YourTurn, machine.NeedsYou);
    }

    [Fact]
    public void AssistantTextFollowedByToolUse_IsNotTurnEnd()
    {
        // 제가 설명을 쓰고 이어서 도구를 호출하는 흔한 경우.
        var machine = new PetStateMachine();
        machine.Apply(new TranscriptEvent(TranscriptEventKind.AssistantText));
        machine.Apply(Tool("Read"));

        Assert.Equal(PetState.Reading, machine.Current);
        Assert.Equal(NeedsYouLevel.None, machine.NeedsYou);
    }

    [Fact]
    public void PermissionPromptNotification_EscalatesToBlocked()
    {
        var machine = new PetStateMachine();
        machine.ApplyNotification("permission_prompt");

        Assert.Equal(PetState.NeedsYou, machine.Current);
        Assert.Equal(NeedsYouLevel.Blocked, machine.NeedsYou);
    }

    [Fact]
    public void IdlePromptNotification_EscalatesToAbandoned()
    {
        var machine = new PetStateMachine();
        machine.ApplyNotification("idle_prompt");
        Assert.Equal(NeedsYouLevel.Abandoned, machine.NeedsYou);
    }

    [Fact]
    public void NewToolUse_ClearsNeedsYou()
    {
        var machine = new PetStateMachine();
        machine.ApplyNotification("idle_prompt");
        machine.Apply(Tool("Read"));

        Assert.Equal(PetState.Reading, machine.Current);
        Assert.Equal(NeedsYouLevel.None, machine.NeedsYou);
    }

    [Fact]
    public void StartsIdle()
    {
        Assert.Equal(PetState.Idle, new PetStateMachine().Current);
    }

    // --- 여러 세션 집계 (스펙 §8) ---

    [Fact]
    public void Aggregate_ShowsWorkingSession_WhenAnotherSessionEndedItsTurn()
    {
        // 세션 A는 내 차례로 끝났고 세션 B는 일하는 중이다.
        // 펫은 일하는 쪽을 연기해야 한다 — 대기 신호를 내면 거짓말이 된다.
        var idleSession = new PetStateMachine();
        idleSession.Apply(new TranscriptEvent(TranscriptEventKind.AssistantText));

        var busySession = new PetStateMachine();
        busySession.Apply(Tool("Write"));

        Assert.Equal(PetState.Writing,
            PetStateMachine.Aggregate(new[] { idleSession, busySession }));
    }

    [Fact]
    public void Aggregate_ShowsNeedsYou_OnlyWhenEverySessionIsWaiting()
    {
        var a = new PetStateMachine();
        a.Apply(new TranscriptEvent(TranscriptEventKind.AssistantText));
        var b = new PetStateMachine();
        b.ApplyNotification("permission_prompt");

        Assert.Equal(PetState.NeedsYou, PetStateMachine.Aggregate(new[] { a, b }));
    }

    [Fact]
    public void Aggregate_PrefersMostRecentlyActiveSession()
    {
        var older = new PetStateMachine();
        older.Apply(Tool("Read"));

        var newer = new PetStateMachine();
        newer.Apply(Tool("Bash"));   // 나중에 적용됨 → Sequence 가 더 큼

        Assert.Equal(PetState.Running,
            PetStateMachine.Aggregate(new[] { older, newer }));
    }

    [Fact]
    public void Aggregate_ReturnsIdle_WhenThereAreNoSessions()
    {
        Assert.Equal(PetState.Idle,
            PetStateMachine.Aggregate(Array.Empty<PetStateMachine>()));
    }
}
```

- [ ] **Step 2: 테스트가 실패하는지 확인**

Run: `dotnet test --filter PetStateMachineTests`
Expected: FAIL — `PetStateMachine`, `NeedsYouLevel` 형식을 찾을 수 없음 (CS0246)

- [ ] **Step 3: 최소 구현**

`src/PetCore/PetStateMachine.cs`:

```csharp
namespace PetCore;

public enum NeedsYouLevel
{
    None = 0,
    YourTurn = 1,    // 턴 종료 — "당신 차례입니다"
    Blocked = 2,     // permission_prompt — "권한 대기로 막혔습니다"
    Abandoned = 3    // idle_prompt (60초) — "아직 안 오셨네요"
}

public sealed class PetStateMachine
{
    private static readonly HashSet<string> ReadingTools =
        new(StringComparer.OrdinalIgnoreCase)
        { "Read", "Grep", "Glob", "WebFetch", "WebSearch" };

    private static readonly HashSet<string> WritingTools =
        new(StringComparer.OrdinalIgnoreCase)
        { "Edit", "Write", "NotebookEdit" };

    private static long _globalSequence;

    public PetState Current { get; private set; } = PetState.Idle;
    public NeedsYouLevel NeedsYou { get; private set; } = NeedsYouLevel.None;

    /// <summary>최근 활동 순서 비교용. 이벤트를 적용할 때마다 증가한다.</summary>
    public long Sequence { get; private set; }

    /// <summary>
    /// 여러 세션의 상태를 하나로 합친다 (스펙 §8).
    /// 일하는 세션이 하나라도 있으면 그중 가장 최근 것을 연기하고,
    /// 모든 세션이 사람을 기다릴 때만 대기 신호를 낸다.
    /// </summary>
    public static PetState Aggregate(IReadOnlyCollection<PetStateMachine> machines)
    {
        if (machines.Count == 0) return PetState.Idle;

        var working = machines
            .Where(m => m.Current != PetState.NeedsYou)
            .OrderByDescending(m => m.Sequence)
            .FirstOrDefault();

        return working?.Current ?? PetState.NeedsYou;
    }

    public void Apply(TranscriptEvent e)
    {
        Sequence = Interlocked.Increment(ref _globalSequence);

        switch (e.Kind)
        {
            case TranscriptEventKind.ToolUse:
                NeedsYou = NeedsYouLevel.None;
                Current = Classify(e.ToolName);
                break;

            case TranscriptEventKind.ToolResult:
                if (e.IsError) Current = PetState.Error;
                break;

            case TranscriptEventKind.AssistantText:
                // tool_use 없는 assistant 메시지 = 턴 종료. 휴리스틱이 아니라 규칙이다.
                Escalate(NeedsYouLevel.YourTurn);
                break;

            case TranscriptEventKind.Thinking:
                NeedsYou = NeedsYouLevel.None;
                Current = PetState.Idle;
                break;
        }
    }

    public void ApplyNotification(string notificationType)
    {
        Sequence = Interlocked.Increment(ref _globalSequence);

        switch (notificationType)
        {
            case "permission_prompt":
                Escalate(NeedsYouLevel.Blocked);
                break;
            case "idle_prompt":
                Escalate(NeedsYouLevel.Abandoned);
                break;
        }
    }

    private void Escalate(NeedsYouLevel level)
    {
        if (level > NeedsYou) NeedsYou = level;
        Current = PetState.NeedsYou;
    }

    private static PetState Classify(string? toolName)
    {
        if (toolName is null) return PetState.Running;
        if (ReadingTools.Contains(toolName)) return PetState.Reading;
        if (WritingTools.Contains(toolName)) return PetState.Writing;
        return PetState.Running;
    }
}
```

- [ ] **Step 4: 테스트 통과 확인**

Run: `dotnet test --filter PetStateMachineTests`
Expected: PASS — Failed: 0, Passed: 21

- [ ] **Step 5: 커밋**

```bash
git add src/PetCore/PetStateMachine.cs tests/PetCore.Tests/PetStateMachineTests.cs
git commit -m "feat: pet state machine with three-level NeedsYou escalation"
```

---

### Task 7: 싱글턴 보장

`SessionStart`는 `startup`·`resume`·`clear`·`compact`·`fork`에서 모두 발생한다. 펫이 여러 마리 뜨면 안 된다.

**Files:**
- Create: `src/PetCore/SingleInstance.cs`
- Test: `tests/PetCore.Tests/SingleInstanceTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces:
  - `PetCore.SingleInstance.TryAcquire(string name, out SingleInstance? instance) -> bool`
  - `SingleInstance : IDisposable`

- [ ] **Step 1: 실패하는 테스트 작성**

`tests/PetCore.Tests/SingleInstanceTests.cs`:

```csharp
using PetCore;
using Xunit;

public class SingleInstanceTests
{
    [Fact]
    public void FirstAcquireSucceeds_SecondFails_ThirdSucceedsAfterRelease()
    {
        var name = $"claude-pet-test-{Guid.NewGuid():N}";

        Assert.True(SingleInstance.TryAcquire(name, out var first));
        Assert.NotNull(first);

        Assert.False(SingleInstance.TryAcquire(name, out var second));
        Assert.Null(second);

        first!.Dispose();

        Assert.True(SingleInstance.TryAcquire(name, out var third));
        third!.Dispose();
    }
}
```

- [ ] **Step 2: 테스트가 실패하는지 확인**

Run: `dotnet test --filter SingleInstanceTests`
Expected: FAIL — `SingleInstance` 형식을 찾을 수 없음 (CS0246)

- [ ] **Step 3: 최소 구현**

`src/PetCore/SingleInstance.cs`:

```csharp
namespace PetCore;

public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private bool _disposed;

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    public static bool TryAcquire(string name, out SingleInstance? instance)
    {
        var mutex = new Mutex(initiallyOwned: false, $"Global\\{name}");
        bool acquired;
        try
        {
            acquired = mutex.WaitOne(TimeSpan.Zero, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            acquired = true;   // 이전 소유자가 죽었다. 우리가 가져간다.
        }

        if (!acquired)
        {
            mutex.Dispose();
            instance = null;
            return false;
        }

        instance = new SingleInstance(mutex);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
```

- [ ] **Step 4: 테스트 통과 확인**

Run: `dotnet test --filter SingleInstanceTests`
Expected: PASS — Failed: 0, Passed: 1

- [ ] **Step 5: 전체 테스트 실행 후 커밋**

```bash
dotnet test
git add src/PetCore/SingleInstance.cs tests/PetCore.Tests/SingleInstanceTests.cs
git commit -m "feat: named-mutex singleton so SessionStart never double-launches"
```

Expected: 전체 통과 — Failed: 0

---

### Task 8: 스프라이트 생성

외부 에셋 의존성과 라이선스 문제를 0으로 만들기 위해 코드로 그린다. 빌드 타임 전용이며 런타임에는 절대 실행되지 않는다.

**Files:**
- Create: `tools/spritegen/spritegen.py`
- Create: `src/PetApp/assets/pet.png` (생성물, 커밋함)

**Interfaces:**
- Consumes: 없음
- Produces: `src/PetApp/assets/pet.png` — 32x32 프레임, 가로 8프레임, 세로 6행.
  **행 순서는 `PetState` enum 순서와 정확히 일치한다**: 0=Idle, 1=Reading, 2=Writing, 3=Running, 4=Error, 5=NeedsYou.

- [ ] **Step 1: 생성기 작성**

`tools/spritegen/spritegen.py`:

```python
"""빌드 타임 스프라이트 생성기. 런타임에는 실행되지 않는다.

산출물: 32x32 프레임 8개 x 6행 = 256x192 PNG.
행 순서는 PetCore.PetState enum 순서와 반드시 일치해야 한다.
"""
import struct
import zlib
from pathlib import Path

FRAME = 32
COLS = 8
ROWS = 6
W, H = FRAME * COLS, FRAME * ROWS

# (몸통, 무늬, 눈) — 행마다 다른 팔레트로 상태를 구분한다.
PALETTES = [
    ((240, 200, 120), (200, 150, 80), (40, 40, 40)),      # 0 Idle
    ((150, 200, 240), (100, 150, 200), (40, 40, 40)),      # 1 Reading
    ((180, 240, 170), (130, 190, 120), (40, 40, 40)),      # 2 Writing
    ((250, 200, 90), (210, 150, 50), (40, 40, 40)),        # 3 Running
    ((240, 130, 130), (200, 80, 80), (255, 255, 255)),     # 4 Error
    ((255, 230, 120), (240, 170, 40), (40, 40, 40)),       # 5 NeedsYou
]


def blank():
    return [[(0, 0, 0, 0)] * W for _ in range(H)]


def draw_cat(px, ox, oy, palette, bob, ear_up):
    body, stripe, eye = palette

    # 몸통
    for y in range(14, 26):
        for x in range(8, 26):
            px[oy + y + bob][ox + x] = (*body, 255)

    # 줄무늬
    for y in range(16, 24, 3):
        for x in range(10, 24):
            px[oy + y + bob][ox + x] = (*stripe, 255)

    # 머리
    for y in range(6, 16):
        for x in range(10, 24):
            px[oy + y + bob][ox + x] = (*body, 255)

    # 귀
    ear_top = 2 if ear_up else 4
    for i in range(4):
        for y in range(ear_top + i, 7):
            px[oy + y + bob][ox + 11 + i] = (*body, 255)
            px[oy + y + bob][ox + 20 - i] = (*body, 255)

    # 눈
    for x in (14, 19):
        px[oy + 10 + bob][ox + x] = (*eye, 255)
        px[oy + 11 + bob][ox + x] = (*eye, 255)

    # 다리
    for x in (10, 15, 18, 23):
        for y in range(26, 29):
            px[oy + y + bob][ox + x] = (*stripe, 255)

    # 꼬리
    for i in range(6):
        px[oy + 24 - i + bob][ox + 26 + (i // 3)] = (*body, 255)


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
    for row, palette in enumerate(PALETTES):
        for col in range(COLS):
            bob = (col // 2) % 2          # 2프레임마다 1px 위아래
            ear_up = row != 0 or col < 4  # Idle 행 후반부는 귀를 내려 조는 느낌
            draw_cat(px, col * FRAME, row * FRAME, palette, bob, ear_up)

    out = Path(__file__).resolve().parents[2] / "src" / "PetApp" / "assets" / "pet.png"
    write_png(out, px)
    print(f"wrote {out} ({W}x{H})")


if __name__ == "__main__":
    main()
```

- [ ] **Step 2: 실행해서 스프라이트 생성**

Run: `python tools/spritegen/spritegen.py`
Expected: `wrote .../src/PetApp/assets/pet.png (256x192)`

- [ ] **Step 3: 생성물 확인**

Run: `python -c "import pathlib; p=pathlib.Path('src/PetApp/assets/pet.png'); print(p.stat().st_size, p.read_bytes()[:8])"`
Expected: 0보다 큰 바이트 수와 `b'\x89PNG\r\n\x1a\n'`

- [ ] **Step 4: 커밋**

```bash
git add tools/spritegen/spritegen.py src/PetApp/assets/pet.png
git commit -m "feat: generate pet sprite sheet from code (no external assets)"
```

---

### Task 9: 방해하지 않는 창

**방해 0의 두 번째 핵심.** 포커스를 훔치지 않고, 클릭을 막지 않고, Alt+Tab에 나타나지 않는 투명 최상위 창을 만든다.

**Files:**
- Create: `src/PetApp/PetApp.csproj`
- Create: `src/PetApp/App.xaml`, `src/PetApp/App.xaml.cs`
- Create: `src/PetApp/NativeMethods.cs`
- Create: `src/PetApp/PetWindow.xaml`, `src/PetApp/PetWindow.xaml.cs`
- Modify: `claude-pet.sln`

**Interfaces:**
- Consumes: `src/PetApp/assets/pet.png`
- Produces:
  - `PetApp.NativeMethods.MakeNonInteractive(IntPtr hwnd) -> void`
  - `PetApp.NativeMethods.IsFullscreenAppForeground() -> bool`
  - `PetApp.PetWindow` — 표시만 담당하는 WPF 창

- [ ] **Step 1: WPF 프로젝트 생성**

```bash
dotnet new wpf -o src/PetApp -f net10.0
dotnet sln add src/PetApp/PetApp.csproj
dotnet add src/PetApp/PetApp.csproj reference src/PetCore/PetCore.csproj
rm -f src/PetApp/MainWindow.xaml src/PetApp/MainWindow.xaml.cs
```

- [ ] **Step 2: 에셋을 리소스로 포함하고 실행 파일명 정하기**

`src/PetApp/PetApp.csproj`의 기존 `<PropertyGroup>` 안에 추가
(훅 스크립트가 `bin/pet.exe`를 찾으므로 지금 이름을 맞춰둔다):

```xml
    <AssemblyName>pet</AssemblyName>
```

같은 파일의 `</Project>` 직전에 추가:

```xml
  <ItemGroup>
    <Resource Include="assets\pet.png" />
  </ItemGroup>
```

- [ ] **Step 3: interop 작성**

`src/PetApp/NativeMethods.cs`:

```csharp
using System;
using System.Runtime.InteropServices;

namespace PetApp;

internal static class NativeMethods
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;   // 마우스 클릭 통과
    private const int WS_EX_TOOLWINDOW  = 0x00000080;   // Alt+Tab 목록에서 제외
    private const int WS_EX_NOACTIVATE  = 0x08000000;   // 절대 포커스를 받지 않음

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    /// <summary>
    /// 창이 포커스를 훔치지도, 클릭을 가로막지도, Alt+Tab에 나타나지도 않게 만든다.
    /// 방해 0 제약의 직접 구현이다.
    /// </summary>
    public static void MakeNonInteractive(IntPtr hwnd)
    {
        var style = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE,
            style | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    /// <summary>전체화면 앱(발표·영상·게임)이 앞에 있으면 펫은 숨어야 한다.</summary>
    public static bool IsFullscreenAppForeground()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        if (!GetWindowRect(foreground, out var windowRect)) return false;

        var monitor = MonitorFromWindow(foreground, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;

        var m = info.rcMonitor;
        return windowRect.Left <= m.Left && windowRect.Top <= m.Top
            && windowRect.Right >= m.Right && windowRect.Bottom >= m.Bottom;
    }
}
```

- [ ] **Step 4: 창 작성**

`src/PetApp/PetWindow.xaml`:

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
        Width="64" Height="64">
    <Image x:Name="Sprite"
           RenderOptions.BitmapScalingMode="NearestNeighbor"
           SnapsToDevicePixels="True" />
</Window>
```

`src/PetApp/PetWindow.xaml.cs`:

```csharp
using System;
using System.Windows;
using System.Windows.Interop;

namespace PetApp;

public partial class PetWindow : Window
{
    public PetWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.MakeNonInteractive(hwnd);
    }
}
```

`src/PetApp/App.xaml`:

```xml
<Application x:Class="PetApp.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             ShutdownMode="OnExplicitShutdown" />
```

`src/PetApp/App.xaml.cs`:

```csharp
using System.Windows;

namespace PetApp;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        new PetWindow().Show();
    }
}
```

- [ ] **Step 5: 빌드 확인**

Run: `dotnet build src/PetApp/PetApp.csproj -c Release`
Expected: 경고 0개, 오류 0개

- [ ] **Step 6: 수동 확인 — 방해 0의 실측**

Run: `dotnet run --project src/PetApp`

아래를 **직접 눈으로 확인한다**. 하나라도 실패하면 다음 태스크로 넘어가지 않는다.

1. 창이 떴을 때 **현재 타이핑하던 창의 포커스가 유지되는가** (가장 중요)
2. 창 영역을 클릭했을 때 **아래 창이 클릭을 받는가**
3. Alt+Tab 목록과 작업 표시줄에 **나타나지 않는가**
4. 배경이 투명한가 (검은 사각형이 보이면 실패)

- [ ] **Step 7: 커밋**

```bash
git add src/PetApp claude-pet.sln
git commit -m "feat: non-interactive transparent topmost window"
```

---

### Task 10: 스프라이트 렌더링과 하단 띠 이동

12fps, 하단 15% 띠, 유휴·가림 시 정지.

**Files:**
- Create: `src/PetApp/SpriteSheet.cs`
- Modify: `src/PetApp/PetWindow.xaml.cs`

**Interfaces:**
- Consumes: `PetCore.PetState`, `assets/pet.png`
- Produces:
  - `PetApp.SpriteSheet.Frame(PetState state, int frameIndex) -> CroppedBitmap`
  - `PetWindow.Render(PetState state)` — 상태를 반영하고 위치를 갱신

- [ ] **Step 1: 시트 슬라이서 작성**

`src/PetApp/SpriteSheet.cs`:

```csharp
using System;
using System.Windows;
using System.Windows.Media.Imaging;
using PetCore;

namespace PetApp;

/// <summary>
/// 32x32 프레임 8개 x 6행. 행 순서는 PetState enum 순서와 일치한다.
/// </summary>
internal sealed class SpriteSheet
{
    public const int FrameSize = 32;
    public const int Columns = 8;

    private readonly BitmapSource _sheet;

    public SpriteSheet()
    {
        _sheet = new BitmapImage(
            new Uri("pack://application:,,,/assets/pet.png", UriKind.Absolute));
    }

    public CroppedBitmap Frame(PetState state, int frameIndex)
    {
        var row = (int)state;
        var col = ((frameIndex % Columns) + Columns) % Columns;
        return new CroppedBitmap(
            _sheet,
            new Int32Rect(col * FrameSize, row * FrameSize, FrameSize, FrameSize));
    }
}
```

- [ ] **Step 2: 창에 렌더 루프 붙이기**

`src/PetApp/PetWindow.xaml.cs`를 다음으로 교체:

```csharp
using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using PetCore;

namespace PetApp;

public partial class PetWindow : Window
{
    private const int Fps = 12;                 // 픽셀아트에 60fps는 과하다
    private const double BandRatio = 0.15;      // 하단 15% 띠
    private const double PixelsPerTick = 3.0;
    private const int SleepAfterTicks = Fps * 20;   // 20초간 Idle이면 잠든다

    private readonly SpriteSheet _sheet = new();
    private readonly DispatcherTimer _timer;

    private int _frame;
    private int _idleTicks;
    private double _x;
    private int _direction = 1;
    private PetState _state = PetState.Idle;

    public PetWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(1000.0 / Fps)
        };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public void SetState(PetState state) => _state = state;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        NativeMethods.MakeNonInteractive(new WindowInteropHelper(this).Handle);

        var work = SystemParameters.WorkArea;
        _x = work.Left + work.Width / 2;
        Top = work.Bottom - Height - work.Height * BandRatio / 2;
    }

    private void Tick()
    {
        // 전체화면 앱이 앞에 있으면 숨고 렌더링을 멈춘다.
        if (NativeMethods.IsFullscreenAppForeground())
        {
            if (Visibility == Visibility.Visible) Visibility = Visibility.Hidden;
            return;
        }
        if (Visibility != Visibility.Visible) Visibility = Visibility.Visible;

        var work = SystemParameters.WorkArea;

        if (_state == PetState.NeedsYou)
        {
            // 하단 중앙으로 모인다. 띠를 벗어나지 않는다.
            var center = work.Left + work.Width / 2 - Width / 2;
            _x += Math.Sign(center - _x) * PixelsPerTick;
        }
        else if (_state != PetState.Idle)
        {
            _x += _direction * PixelsPerTick;
            if (_x <= work.Left) { _x = work.Left; _direction = 1; }
            if (_x >= work.Right - Width) { _x = work.Right - Width; _direction = -1; }
        }

        Left = _x;
        Top = work.Bottom - Height - work.Height * BandRatio / 2;

        // 잠들면 렌더링을 멈춘다 (스펙 §6.5). 프레임을 갱신하지 않으면
        // WPF는 다시 그리지 않으므로 CPU가 사실상 0으로 떨어진다.
        _idleTicks = _state == PetState.Idle ? _idleTicks + 1 : 0;
        if (_idleTicks > SleepAfterTicks) return;

        _frame++;
        Sprite.Source = _sheet.Frame(_state, _frame);
    }
}
```

- [ ] **Step 3: 빌드 및 실행**

Run: `dotnet run --project src/PetApp`
Expected: 고양이가 화면 하단 띠에서 애니메이션한다

- [ ] **Step 4: 자원 예산 확인**

작업 관리자에서 `pet` 프로세스를 확인한다.

1. 배회 중 CPU: 1% 미만
2. 20초 이상 방치해 잠든 뒤 CPU: 사실상 0% (프레임 갱신이 멈추므로)
3. 전체화면 앱(예: 브라우저 F11)을 앞으로 가져오면 펫이 사라지는가

- [ ] **Step 5: 커밋**

```bash
git add src/PetApp/SpriteSheet.cs src/PetApp/PetWindow.xaml.cs
git commit -m "feat: 12fps sprite rendering confined to bottom screen band"
```

---

### Task 11: 플러그인 패키지와 훅 스크립트

**훅은 어떤 입력에도 exit 0으로 끝난다.** 장식용 펫이 세션을 망가뜨릴 권한을 갖게 해서는 안 된다.

**Files:**
- Create: `plugin/.claude-plugin/plugin.json`
- Create: `plugin/hooks/hooks.json`
- Create: `plugin/hooks/session_start.ps1`
- Create: `plugin/hooks/notification.ps1`
- Create: `plugin/hooks/session_end.ps1`

**Interfaces:**
- Consumes: `PetCore.SessionRecord`의 JSON 형식 (`sessionId`, `transcriptPath`, `pid`, `pidStartUnixMs`, `touchedUnixMs`)
- Produces: `${CLAUDE_PLUGIN_DATA}/sessions/<session_id>.json`, `${CLAUDE_PLUGIN_DATA}/notify/<timestamp>.json`

- [ ] **Step 1: 플러그인 매니페스트 작성**

`plugin/.claude-plugin/plugin.json`:

```json
{
  "name": "claude-pet",
  "version": "0.1.0",
  "description": "Desktop pet that reacts to Claude Code activity without interfering with it"
}
```

- [ ] **Step 2: 훅 정의 작성**

`plugin/hooks/hooks.json`:

```json
{
  "hooks": {
    "SessionStart": [
      {
        "hooks": [
          {
            "type": "command",
            "shell": "powershell",
            "async": true,
            "command": "& '${CLAUDE_PLUGIN_ROOT}/hooks/session_start.ps1'"
          }
        ]
      }
    ],
    "Notification": [
      {
        "matcher": "permission_prompt|idle_prompt",
        "hooks": [
          {
            "type": "command",
            "shell": "powershell",
            "async": true,
            "command": "& '${CLAUDE_PLUGIN_ROOT}/hooks/notification.ps1'"
          }
        ]
      }
    ],
    "SessionEnd": [
      {
        "hooks": [
          {
            "type": "command",
            "shell": "powershell",
            "async": true,
            "command": "& '${CLAUDE_PLUGIN_ROOT}/hooks/session_end.ps1'"
          }
        ]
      }
    ]
  }
}
```

- [ ] **Step 3: SessionStart 훅 작성**

`plugin/hooks/session_start.ps1`:

```powershell
# 세션을 등록하고 펫이 없으면 띄운다.
# 어떤 경우에도 exit 0 으로 끝난다 — 펫이 세션을 방해해서는 안 된다.
try {
    $raw = [Console]::In.ReadToEnd()
    $payload = $raw | ConvertFrom-Json

    $dataDir = $env:CLAUDE_PLUGIN_DATA
    if (-not $dataDir) { exit 0 }

    $sessionDir = Join-Path $dataDir 'sessions'
    New-Item -ItemType Directory -Force -Path $sessionDir | Out-Null

    # Claude Code 프로세스를 찾는다. 훅은 그 자손으로 실행된다.
    $pidValue = 0
    $startMs = 0
    $current = Get-CimInstance Win32_Process -Filter "ProcessId=$PID"
    for ($i = 0; $i -lt 6 -and $current; $i++) {
        if ($current.Name -match '^(claude|node)') {
            $pidValue = [int]$current.ProcessId
            $startMs = [DateTimeOffset]::new($current.CreationDate.ToUniversalTime(), [TimeSpan]::Zero).ToUnixTimeMilliseconds()
            break
        }
        $current = Get-CimInstance Win32_Process -Filter "ProcessId=$($current.ParentProcessId)" -ErrorAction SilentlyContinue
    }

    $record = [ordered]@{
        sessionId      = $payload.session_id
        transcriptPath = $payload.transcript_path
        pid            = $pidValue
        pidStartUnixMs = $startMs
        touchedUnixMs  = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    }

    $target = Join-Path $sessionDir "$($payload.session_id).json"
    $temp = "$target.tmp"
    $record | ConvertTo-Json -Compress | Set-Content -Path $temp -Encoding utf8
    Move-Item -Force -Path $temp -Destination $target

    # 펫 기동. 이미 떠 있으면 뮤텍스가 막으므로 그냥 시도한다.
    $exe = Join-Path $env:CLAUDE_PLUGIN_ROOT 'bin/pet.exe'
    if (Test-Path $exe) {
        Start-Process -FilePath $exe -WindowStyle Hidden -ErrorAction SilentlyContinue
    }
}
catch {
    # 삼킨다. 절대 세션을 방해하지 않는다.
}
exit 0
```

- [ ] **Step 4: Notification 훅 작성**

`plugin/hooks/notification.ps1`:

```powershell
# 사람이 필요한 순간을 펫에게 알린다.
# 이 훅은 사용자가 코딩하고 있지 않을 때만 발생한다.
try {
    $payload = [Console]::In.ReadToEnd() | ConvertFrom-Json

    $dataDir = $env:CLAUDE_PLUGIN_DATA
    if (-not $dataDir) { exit 0 }

    $notifyDir = Join-Path $dataDir 'notify'
    New-Item -ItemType Directory -Force -Path $notifyDir | Out-Null

    $stamp = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $record = [ordered]@{
        sessionId        = $payload.session_id
        notificationType = $payload.notification_type
        atUnixMs         = $stamp
    }

    $target = Join-Path $notifyDir "$stamp.json"
    $record | ConvertTo-Json -Compress | Set-Content -Path $target -Encoding utf8
}
catch { }
exit 0
```

- [ ] **Step 5: SessionEnd 훅 작성**

`plugin/hooks/session_end.ps1`:

```powershell
# 세션 등록을 해제한다. 이것은 빠른 길일 뿐이며 권위가 아니다.
# 실패해도 펫의 PID 워치독이 정리한다.
try {
    $payload = [Console]::In.ReadToEnd() | ConvertFrom-Json

    $dataDir = $env:CLAUDE_PLUGIN_DATA
    if (-not $dataDir) { exit 0 }

    $target = Join-Path $dataDir "sessions/$($payload.session_id).json"
    if (Test-Path $target) {
        Remove-Item -Force -Path $target -ErrorAction SilentlyContinue
    }
}
catch { }
exit 0
```

- [ ] **Step 6: 계약 테스트 — 훅이 올바른 형식을 쓰는가**

Run:

```powershell
$env:CLAUDE_PLUGIN_DATA = "$env:TEMP\pet-hook-test"
Remove-Item -Recurse -Force $env:CLAUDE_PLUGIN_DATA -ErrorAction SilentlyContinue
'{"session_id":"test123","transcript_path":"C:\\t\\a.jsonl","cwd":"C:\\p","hook_event_name":"SessionStart"}' |
  & .\plugin\hooks\session_start.ps1
Get-Content "$env:CLAUDE_PLUGIN_DATA\sessions\test123.json"
```

Expected: `{"sessionId":"test123","transcriptPath":"C:\\t\\a.jsonl","pid":...,"pidStartUnixMs":...,"touchedUnixMs":...}`

- [ ] **Step 7: 계약 테스트 — 잘못된 입력에도 exit 0인가**

Run:

```powershell
'not json at all' | & .\plugin\hooks\session_start.ps1; Write-Host "exit=$LASTEXITCODE"
'' | & .\plugin\hooks\notification.ps1; Write-Host "exit=$LASTEXITCODE"
'' | & .\plugin\hooks\session_end.ps1; Write-Host "exit=$LASTEXITCODE"
```

Expected: 세 줄 모두 `exit=0`

- [ ] **Step 8: 커밋**

```bash
git add plugin
git commit -m "feat: plugin package with three async hooks that never fail"
```

---

### Task 12: 배선

`PetCore`의 조각들을 `PetApp`에 연결한다. 여기서 프로세스 우선순위를 낮추고 싱글턴을 건다.

**Files:**
- Modify: `src/PetApp/App.xaml.cs`
- Create: `src/PetApp/PetHost.cs`

**Interfaces:**
- Consumes: `SingleInstance`, `SessionRegistry`, `Watchdog`, `WindowsProcessProbe`, `TranscriptTail`, `PetStateMachine`, `PetWindow.SetState`
- Produces: 동작하는 `pet.exe`

- [ ] **Step 1: 호스트 작성**

`src/PetApp/PetHost.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Threading;
using PetCore;

namespace PetApp;

/// <summary>
/// PetCore 조각들을 묶어 창에 상태를 공급한다.
/// 폴링 주기는 1초 — Claude Code 쪽에 비용을 전혀 만들지 않는다.
/// </summary>
internal sealed class PetHost
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(10);

    private readonly PetWindow _window;
    private readonly string _dataDir;
    private readonly SessionRegistry _registry;
    private readonly Watchdog _watchdog;
    // 세션마다 독립된 상태를 유지한다. 하나로 공유하면 세션 A의 턴 종료가
    // 세션 B의 작업 중 상태를 덮어써서 거짓 대기 신호가 나온다 (스펙 §8).
    private readonly Dictionary<string, TranscriptTail> _tails = new();
    private readonly Dictionary<string, PetStateMachine> _machines = new();
    private readonly DispatcherTimer _timer;

    private long _lastNotifyMs;

    public PetHost(PetWindow window, string dataDir)
    {
        _window = window;
        _dataDir = dataDir;
        _registry = new SessionRegistry(Path.Combine(dataDir, "sessions"));
        _watchdog = new Watchdog(new WindowsProcessProbe(), Grace);

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += (_, _) => Poll();
    }

    public void Start() => _timer.Start();

    private void Poll()
    {
        var sessions = _registry.ReadAll();

        if (_watchdog.ShouldExit(sessions, DateTimeOffset.UtcNow))
        {
            _timer.Stop();
            System.Windows.Application.Current.Shutdown();
            return;
        }

        var liveIds = new HashSet<string>();

        foreach (var session in sessions)
        {
            liveIds.Add(session.SessionId);

            if (!_tails.TryGetValue(session.SessionId, out var tail))
            {
                tail = new TranscriptTail(session.TranscriptPath);
                _tails[session.SessionId] = tail;
                _machines[session.SessionId] = new PetStateMachine();
            }

            var machine = _machines[session.SessionId];
            foreach (var e in tail.ReadNew())
                machine.Apply(e);
        }

        // 사라진 세션은 정리한다. 그러지 않으면 죽은 세션의 상태가 집계에 계속 남는다.
        foreach (var goneId in _tails.Keys.Where(id => !liveIds.Contains(id)).ToList())
        {
            _tails.Remove(goneId);
            _machines.Remove(goneId);
        }

        DrainNotifications();
        _window.SetState(PetStateMachine.Aggregate(_machines.Values));
    }

    private void DrainNotifications()
    {
        var notifyDir = Path.Combine(_dataDir, "notify");
        if (!Directory.Exists(notifyDir)) return;

        foreach (var file in Directory.EnumerateFiles(notifyDir, "*.json"))
        {
            try
            {
                if (!long.TryParse(Path.GetFileNameWithoutExtension(file), out var stamp))
                    continue;
                if (stamp <= _lastNotifyMs) { File.Delete(file); continue; }

                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;

                // 알림을 해당 세션의 상태 머신으로 보낸다.
                // 세션을 알 수 없으면 무시한다 — 엉뚱한 세션에 대기 신호를 붙이면 안 된다.
                if (root.TryGetProperty("notificationType", out var type)
                    && root.TryGetProperty("sessionId", out var sid)
                    && sid.GetString() is { } sessionId
                    && _machines.TryGetValue(sessionId, out var machine))
                {
                    machine.ApplyNotification(type.GetString() ?? "");
                }

                _lastNotifyMs = stamp;
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // 훅이 쓰는 중일 수 있다. 다음 주기에 다시 본다.
            }
        }
    }
}
```

- [ ] **Step 2: 진입점 교체**

`src/PetApp/App.xaml.cs`:

```csharp
using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using PetCore;

namespace PetApp;

public partial class App : Application
{
    private SingleInstance? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 펫은 한 마리. SessionStart 는 startup/resume/clear/compact/fork 에서 모두 발생한다.
        if (!SingleInstance.TryAcquire("claude-pet", out _instance))
        {
            Shutdown();
            return;
        }

        // 빌드·테스트와 CPU를 두고 경쟁하지 않는다.
        try
        {
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
        }

        var dataDir = Environment.GetEnvironmentVariable("CLAUDE_PLUGIN_DATA")
                      ?? Path.Combine(
                          Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                          "claude-pet");

        var window = new PetWindow();
        window.Show();
        new PetHost(window, dataDir).Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}
```

- [ ] **Step 3: 전체 빌드와 테스트**

Run: `dotnet build -c Release; dotnet test`
Expected: 빌드 오류 0개, 테스트 Failed: 0

- [ ] **Step 4: 단일 실행 파일로 발행**

Run:

```bash
dotnet publish src/PetApp/PetApp.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o plugin/bin
ls plugin/bin/pet.exe
```

Expected: `plugin/bin/pet.exe` 존재 (Task 9에서 `AssemblyName`을 `pet`으로 지정했다)

- [ ] **Step 5: 종단 확인 — 워치독이 실제로 동작하는가**

Run:

```powershell
$env:CLAUDE_PLUGIN_DATA = "$env:TEMP\pet-e2e"
Remove-Item -Recurse -Force $env:CLAUDE_PLUGIN_DATA -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$env:CLAUDE_PLUGIN_DATA\sessions" | Out-Null
Start-Process .\plugin\bin\pet.exe
Start-Sleep -Seconds 15
Get-Process pet -ErrorAction SilentlyContinue
```

Expected: 등록된 세션이 없으므로 유예 10초 후 펫이 스스로 종료 — `Get-Process`가 아무것도 반환하지 않는다

- [ ] **Step 6: 커밋**

```bash
git add src/PetApp plugin
git commit -m "feat: wire PetCore into PetApp with singleton and watchdog"
```

---

### Task 13: 방해 0 측정

**"방해되지 않습니다"는 말로는 부족하다. 숫자가 나오기 전까지 달성했다고 말하지 않는다.**

**Files:**
- Create: `bench/measure_overhead.ps1`
- Create: `docs/superpowers/plans/2026-08-28-claude-pet-measurements.md`

**Interfaces:**
- Consumes: `plugin/bin/pet.exe`
- Produces: 측정 결과 문서

- [ ] **Step 1: 측정 스크립트 작성**

`bench/measure_overhead.ps1`:

```powershell
<#
.SYNOPSIS
  펫 ON/OFF 상태에서 파일 I/O 지연과 유휴 CPU를 비교한다.
  스펙 10.4 "방해 0 측정"의 구현이다.
#>
param([int]$Iterations = 2000)

function Measure-WriteLatency {
    param([int]$Count)
    $path = Join-Path $env:TEMP "pet-bench-$(New-Guid).jsonl"
    $line = '{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t","name":"Read","input":{}}]}}'
    $samples = New-Object 'System.Collections.Generic.List[double]'

    $stream = [System.IO.FileStream]::new(
        $path, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete)
    $writer = [System.IO.StreamWriter]::new($stream)

    for ($i = 0; $i -lt $Count; $i++) {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $writer.WriteLine($line)
        $writer.Flush()
        $sw.Stop()
        $samples.Add($sw.Elapsed.TotalMilliseconds)
    }

    $writer.Dispose(); $stream.Dispose()
    Remove-Item -Force $path -ErrorAction SilentlyContinue

    $sorted = $samples | Sort-Object
    [pscustomobject]@{
        Median = $sorted[[int]($sorted.Count * 0.50)]
        P95    = $sorted[[int]($sorted.Count * 0.95)]
        P99    = $sorted[[int]($sorted.Count * 0.99)]
    }
}

Write-Host "=== 펫 OFF ==="
Get-Process pet -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
$off = Measure-WriteLatency -Count $Iterations
$off | Format-List

Write-Host "=== 펫 ON ==="
$env:CLAUDE_PLUGIN_DATA = "$env:TEMP\pet-bench-data"
New-Item -ItemType Directory -Force "$env:CLAUDE_PLUGIN_DATA\sessions" | Out-Null
Start-Process .\plugin\bin\pet.exe
Start-Sleep -Seconds 3

$petProcess = Get-Process pet -ErrorAction SilentlyContinue
$on = Measure-WriteLatency -Count $Iterations
$on | Format-List

if ($petProcess) {
    $cpuBefore = $petProcess.TotalProcessorTime
    Start-Sleep -Seconds 10
    $petProcess.Refresh()
    $cpuPercent = ($petProcess.TotalProcessorTime - $cpuBefore).TotalSeconds / 10 * 100
    Write-Host ("유휴 CPU: {0:N2}%" -f $cpuPercent)
}

Write-Host ""
Write-Host ("중앙값 차이: {0:N4} ms" -f ($on.Median - $off.Median))
Write-Host ("P99 차이:    {0:N4} ms" -f ($on.P99 - $off.P99))
```

- [ ] **Step 2: 측정 실행**

Run: `powershell -File bench/measure_overhead.ps1`
Expected: OFF/ON 각각의 중앙값·P95·P99, 유휴 CPU 퍼센트, 두 차이값 출력

- [ ] **Step 3: 합격 기준 판정**

| 항목 | 기준 |
|---|---|
| 쓰기 지연 중앙값 차이 | 측정 노이즈 범위 내 (재실행 시 부호가 뒤집힐 정도) |
| P99 차이 | 1 ms 미만 |
| 유휴 CPU | 1% 미만 |

**하나라도 기준을 넘으면 원인을 찾아 고친 뒤 재측정한다. 기준을 낮추지 않는다.**

- [ ] **Step 4: 결과 기록**

`docs/superpowers/plans/2026-08-28-claude-pet-measurements.md`에 실제 출력값을 붙여넣고,
기준 통과 여부를 명시한다. 추정치가 아니라 실측값을 기록한다.

- [ ] **Step 5: 커밋**

```bash
git add bench docs/superpowers/plans/2026-08-28-claude-pet-measurements.md
git commit -m "test: measure and document zero-interference claim"
```

---

## 스펙 §11 미해결 항목의 처리

스펙이 "구현 초반에 확인할 것"으로 남긴 세 항목의 처리 방침이다.
**세 항목 모두 안전한 기본값이 이 계획에 이미 구현되어 있으므로, 확인은 태스크가 아니라
1단계 완료 후의 선택적 개선으로 미룬다.** 미루는 것을 명시적으로 기록해 잊히지 않게 한다.

| 항목 | 이 계획의 기본값 | 재검토 조건 |
|---|---|---|
| 투명 최상위 창의 렌더링 품질 | — | **미루지 않는다.** Task 9 Step 6에서 반드시 눈으로 확인한다. |
| 기동 수단: async `SessionStart` 훅 vs `monitors/monitors.json` | async `SessionStart` 훅 (Task 11) | 훅 기동이 불안정하다는 증거가 나오면 모니터로 교체한다. 워치독이 권위라는 구조는 어느 쪽이든 그대로다. |
| `PermissionRequest` 훅의 즉시성 | `Notification`/`permission_prompt` (약 6초 지연) | 6초 지연이 실제로 답답하다고 느껴지면, `PermissionRequest`를 신호로만 쓸 때 권한 결정에 부작용이 없는지 확인한 뒤 올린다. |

## 완료 조건

모든 태스크의 체크박스가 채워지고, 아래가 전부 참일 때 1단계가 끝난다.

- [ ] `dotnet test` 전부 통과
- [ ] Task 9 Step 6의 수동 확인 4항목 통과 (포커스·클릭·Alt+Tab·투명도)
- [ ] Task 13의 측정 3항목이 기준 통과, 실측값이 문서에 기록됨
- [ ] 펫이 세션 시작 시 등장하고, 모든 세션 종료 후 유예를 거쳐 사라짐
- [ ] `/clear`와 `/compact` 시 펫이 깜빡이지 않음
