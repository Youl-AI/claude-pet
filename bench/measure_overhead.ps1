<#
.SYNOPSIS
  펫 ON/OFF 상태에서 파일 쓰기 지연·CPU 바운드 연산 지연·유휴 CPU를 비교한다.
  스펙 10.4 "방해 0 측정"의 구현이다.

.DESCRIPTION
  "방해되지 않습니다"는 말로는 부족하다 — 숫자가 나오기 전까지 달성했다고 말하지 않는다.
  이 스크립트는 다음을 지킨다.

  - 평균이 아니라 분포(중앙값/P95/P99)를 보고한다. 평균은 사용자가 실제로 느끼는
    끊김을 감춘다.
  - ON/OFF 블록을 무작위로 뒤섞어 인터리브한다. "OFF 다 재고 나서 ON 잰다"는 순서는
    도중에 끼어드는 배경 부하를 전부 펫 탓으로 돌리게 된다.
  - CPU는 코어당(%)과 시스템 전체(%)를 모두, 논리 프로세서 수와 함께 명시한다.
    "2.21%"와 "1% 미만" 기준이 같은 기준으로 비교 가능한지 애매했던 문제를 없앤다.
  - 펫이 실제로 떠 있고 실제로 움직이는지 확인한다. 펫은 Idle 상태로 20초가 지나면
    스스로 잠들어 렌더링을 멈춘다 — 확인 없이 재면 "조용히 죽었거나 이미 잠든" 상태를
    "방해 0"으로 오인할 수 있다. 이 스크립트는 배회(wandering)·잠듦(asleep)·활동 중
    (working) 세 CPU 구간을 의도적으로 분리해서 잰다.
  - 펫에게는 하네스가 직접 통제하는 트랜스크립트를 가리키는 합성 세션을 등록한다
    (SessionStart 훅 없이도 워치독이 펫을 살려두게).
  - 이 기기에는 무관한 pet.exe(VS Code Python 확장의 python-env-tools)가 함께 떠
    있을 수 있다 — 프로세스 이름이 아니라 전체 경로로만 우리 펫을 구분한다.
  - 시작한 펫 프로세스와 임시 디렉터리를 스크립트 종료 시 항상 정리한다.

  측정 대상 워크로드는 두 축이다.
  1. 파일 쓰기 지연 — 펫이 실제로 Claude Code와 공유하는 자원(JSONL 트랜스크립트)에
     대한 쓰기. ON 조건에서는 펫이 실제로 tail 하고 있는 바로 그 파일에 쓴다.
  2. CPU 바운드 연산 지연 — SHA-256 반복 해시. 설계서 §6.5가 명시하는 "빌드·테스트와
     CPU 경쟁하지 않음"을 파일 I/O가 아닌 순수 스케줄링 경합으로 확인하는 두 번째 축.

.NOTES
  Windows PowerShell 5.1 대상. &&/||/삼항연산자/?? 없음.
#>
param(
    [int]$Iterations = 2000,          # 조건(ON/OFF)당 파일 쓰기 지연 샘플 총합
    [int]$ComputeIterations = 2000,   # 조건당 CPU 바운드 연산 샘플 총합
    [int]$Blocks = 8,                 # 인터리브 블록 수 (짝수, ON/OFF 절반씩)
    [int]$WanderCpuSampleSeconds = 12,   # 20초 잠듦 임계값보다 반드시 짧아야 한다
    [int]$SleepPreBufferSeconds = 10,    # 잠듦 임계값을 확실히 넘기기 위한 대기
    [int]$SleepCpuSampleSeconds = 10,
    [int]$WorkingCpuSampleSeconds = 10,
    [int]$WorkingEventIntervalSeconds = 2,  # 20초 미만이어야 활동 중에도 잠들지 않는다
    [switch]$KeepScratch
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ============================================================
# 0. 환경 설정
# ============================================================

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot  = Split-Path -Parent $scriptDir
$petExe    = Join-Path $repoRoot 'plugin\bin\pet.exe'

if (-not (Test-Path $petExe)) {
    throw "pet.exe가 없습니다: $petExe`n먼저 다음으로 빌드하세요:`n  dotnet publish src/PetApp/PetApp.csproj -c Release -r win-x64 -p:SelfContained=false -p:PublishSingleFile=true -o plugin/bin"
}
# 심볼릭 링크·상대 경로를 정규화한 전체 경로. 이 경로로만 "우리" pet.exe를 구분한다 —
# 이 기기에는 VS Code Python 확장(ms-python.vscode-python-envs-...\pet.exe)이 같은
# 프로세스 이름으로 떠 있을 수 있고, 이름만으로 찾으면 그걸 잘못 죽이거나 잘못 잰다.
$petExeFull = (Resolve-Path $petExe).Path
$logicalProcessors = [Environment]::ProcessorCount

if ($Blocks % 2 -ne 0) {
    throw "Blocks는 짝수여야 합니다 (ON/OFF 절반씩): $Blocks"
}
# Start-OurPet의 1.5초 정착 대기 + CPU 구간의 2초 워밍업 대기를 더한 뒤에
# 배회 표본이 시작된다 — 그 시작 시점부터도 표본이 끝나기 전에 20초 잠듦
# 임계값을 넘으면 "배회" 표본에 "잠듦"이 섞여 들어간다. 여유 있게 넉넉히
# 잡는다(3.5초 사전 대기 + 표본 시간 < 20초).
$wanderPreDelaySeconds = 3.5
if (($wanderPreDelaySeconds + $WanderCpuSampleSeconds) -ge 20) {
    throw "WanderCpuSampleSeconds가 너무 큽니다 — 정착/워밍업 대기($wanderPreDelaySeconds`s)를 더하면 20초(잠듦 임계값)를 넘습니다: $WanderCpuSampleSeconds"
}
if ($WorkingEventIntervalSeconds -ge 20) {
    throw "WorkingEventIntervalSeconds는 20초 미만이어야 합니다: $WorkingEventIntervalSeconds"
}

Write-Host "=== 환경 ==="
Write-Host ("pet.exe:          {0}" -f $petExeFull)
Write-Host ("논리 프로세서 수: {0}  (CPU %는 코어당/시스템전체 둘 다 아래에 보고한다)" -f $logicalProcessors)
Write-Host ("Blocks:           {0} (ON {1} / OFF {1}, 무작위 순서)" -f $Blocks, ($Blocks / 2))
Write-Host ""

$scratchRoot = Join-Path $env:TEMP ("pet-bench-" + [guid]::NewGuid().ToString('N'))
$dataDir     = Join-Path $scratchRoot 'data'
$sessionsDir = Join-Path $dataDir 'sessions'
$transcriptPath = Join-Path $scratchRoot 'transcript.jsonl'
$cpuTranscriptPath = Join-Path $scratchRoot 'cpu-phase-transcript.jsonl'

New-Item -ItemType Directory -Force -Path $sessionsDir | Out-Null
New-Item -ItemType File -Force -Path $transcriptPath | Out-Null
New-Item -ItemType File -Force -Path $cpuTranscriptPath | Out-Null

$anySuspicious = $false

function Write-Suspicious {
    param([string]$Message)
    $script:anySuspicious = $true
    Write-Host ("[의심스러움] {0}" -f $Message) -ForegroundColor Yellow
}

# ============================================================
# 1. 프로세스 도우미 — 반드시 전체 경로로만 우리 펫을 구분한다
# ============================================================

function Get-OurPetProcess {
    Get-Process -Name pet -ErrorAction SilentlyContinue | Where-Object {
        $p = $null
        try { $p = $_.Path } catch { $p = $null }
        [bool]$p -and ($p -ieq $petExeFull)
    }
}

function Wait-ForPetState {
    param([bool]$ShouldExist, [int]$TimeoutSeconds = 10)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $procs = Get-OurPetProcess
    while ((Get-Date) -lt $deadline) {
        $procs = Get-OurPetProcess
        $exists = [bool]$procs
        if ($exists -eq $ShouldExist) { return $procs }
        Start-Sleep -Milliseconds 200
    }
    return $procs
}

function Stop-OurPet {
    $procs = Get-OurPetProcess
    if ($procs) {
        $procs | Stop-Process -Force -ErrorAction SilentlyContinue
    }
    Wait-ForPetState -ShouldExist:$false -TimeoutSeconds 10 | Out-Null
}

function Start-OurPet {
    param([string]$DataDir)
    Stop-OurPet
    $env:CLAUDE_PLUGIN_DATA = $DataDir
    Start-Process -FilePath $petExeFull -WindowStyle Hidden
    $procs = Wait-ForPetState -ShouldExist:$true -TimeoutSeconds 10
    if (-not $procs) {
        throw "pet.exe가 뜨지 않았습니다 (뮤텍스 충돌 또는 시작 실패). 이미 실행 중인 인스턴스가 있는지 확인하세요."
    }
    $proc = @($procs)[0]
    # PetHost 폴링 주기는 1초 — 최소 한 번의 폴링(세션 부착 + SkipToEnd)이
    # 지나가도록 여유를 둔다.
    Start-Sleep -Milliseconds 1500
    $proc.Refresh()
    if ($proc.HasExited) {
        throw "펫이 뜨자마자 종료되었습니다 (PID $($proc.Id))."
    }
    if (-not $proc.Responding) {
        Write-Suspicious "펫 프로세스(PID $($proc.Id))가 시작 직후 Responding=False 입니다."
    }
    return $proc
}

# ============================================================
# 2. 세션 등록 — 합성 세션, 하네스가 통제하는 트랜스크립트를 가리킨다
# ============================================================
#
# 워치독은 PID 생존을 권위로 삼는다(설계서 §7.2). 등록된 PID로 이 PowerShell
# 프로세스 자신($PID)을 쓴다 — 이 스크립트가 끝날 때까지 반드시 살아있고,
# StartTime을 정확히 알 수 있어 WindowsProcessProbe의 ±2초 허용오차 안에서
# "생존"으로 판정된다. SessionStart 훅은 전혀 거치지 않는다.

function Register-SyntheticSession {
    param([string]$SessionId, [string]$TranscriptPath)
    $self = Get-Process -Id $PID
    $startMs = [DateTimeOffset]::new($self.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds()
    $record = [ordered]@{
        sessionId      = $SessionId
        transcriptPath = $TranscriptPath
        pid            = $PID
        pidStartUnixMs = $startMs
        touchedUnixMs  = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    }
    $target = Join-Path $sessionsDir "$SessionId.json"
    $record | ConvertTo-Json -Compress | Set-Content -Path $target -Encoding utf8
    return $target
}

function Unregister-SyntheticSession {
    param([string]$SessionFile)
    if (Test-Path $SessionFile) {
        Remove-Item -Force -Path $SessionFile -ErrorAction SilentlyContinue
    }
}

$mainSessionFile = $null
$cpuSessionFile = $null

# try/finally로 본문 전체를 감싼다 — 중간 어디서 예외가 나도(펫이 안 뜬다,
# 프로세스가 사라진다 등) 시작한 펫 프로세스와 임시 디렉터리는 반드시 정리된다.
try {

$mainSessionId = "bench-main-" + [guid]::NewGuid().ToString('N')
$mainSessionFile = Register-SyntheticSession -SessionId $mainSessionId -TranscriptPath $transcriptPath

# ============================================================
# 3. 워크로드
# ============================================================

function Add-ToolUseLine {
    param([string]$TranscriptPath, [string]$ToolName = 'Bench')
    $line = '{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t","name":"' + $ToolName + '","input":{}}]}}'
    Add-Content -Path $TranscriptPath -Value $line -Encoding utf8
}

function Measure-WriteLatencySamples {
    param([string]$Path, [int]$Count)
    $samples = New-Object 'System.Collections.Generic.List[double]'
    $line = '{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t","name":"Bench","input":{}}]}}'
    # Append 모드: 여러 블록에 걸쳐 같은 트랜스크립트 파일에 이어 쓴다 — Claude Code가
    # 실제로 트랜스크립트를 다루는 방식(이어쓰기)과 같고, ON 조건에서는 펫이 바로 이
    # 파일을 동시에 읽는다(설계서 §6.2 파일 잠금 없음의 실사용 시나리오).
    $stream = [System.IO.FileStream]::new(
        $Path, [System.IO.FileMode]::Append, [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete)
    $writer = [System.IO.StreamWriter]::new($stream)
    for ($i = 0; $i -lt $Count; $i++) {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $writer.WriteLine($line)
        $writer.Flush()
        $sw.Stop()
        $samples.Add($sw.Elapsed.TotalMilliseconds)
    }
    $writer.Dispose()
    $stream.Dispose()
    return ,$samples.ToArray()
}

function Measure-ComputeLatencySamples {
    # CPU 바운드 두 번째 축: SHA-256 반복 해시. 파일 I/O와 무관하게, BelowNormal
    # 우선순위의 펫이 같은 코어를 두고 스케줄링 경합을 일으키는지를 잰다
    # (설계서 §6.5 "빌드·테스트와 CPU 경쟁하지 않음").
    param([int]$Count)
    $samples = New-Object 'System.Collections.Generic.List[double]'
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $buffer = New-Object byte[] (256 * 1024)
    for ($i = 0; $i -lt $buffer.Length; $i++) { $buffer[$i] = [byte]($i % 256) }
    for ($i = 0; $i -lt $Count; $i++) {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        [void]$sha.ComputeHash($buffer)
        $sw.Stop()
        $samples.Add($sw.Elapsed.TotalMilliseconds)
    }
    $sha.Dispose()
    return ,$samples.ToArray()
}

function Get-Distribution {
    param([double[]]$Samples)
    $sorted = @($Samples | Sort-Object)
    $n = $sorted.Count
    if ($n -eq 0) {
        return [pscustomobject]@{ Count = 0; Mean = 0.0; Median = 0.0; P95 = 0.0; P99 = 0.0; Min = 0.0; Max = 0.0 }
    }
    $sum = 0.0
    foreach ($s in $sorted) { $sum += $s }
    $mean = $sum / $n

    $idxMedian = [int][Math]::Ceiling(0.50 * $n) - 1
    $idxP95    = [int][Math]::Ceiling(0.95 * $n) - 1
    $idxP99    = [int][Math]::Ceiling(0.99 * $n) - 1
    if ($idxMedian -lt 0) { $idxMedian = 0 }
    if ($idxP95 -lt 0) { $idxP95 = 0 }
    if ($idxP99 -lt 0) { $idxP99 = 0 }
    if ($idxMedian -gt ($n - 1)) { $idxMedian = $n - 1 }
    if ($idxP95 -gt ($n - 1)) { $idxP95 = $n - 1 }
    if ($idxP99 -gt ($n - 1)) { $idxP99 = $n - 1 }

    [pscustomobject]@{
        Count  = $n
        Mean   = $mean
        Median = $sorted[$idxMedian]
        P95    = $sorted[$idxP95]
        P99    = $sorted[$idxP99]
        Min    = $sorted[0]
        Max    = $sorted[$n - 1]
    }
}

function Format-Distribution {
    param([string]$Label, $Dist)
    Write-Host ("  {0,-28} n={1,-6} mean={2,8:N4}ms  median={3,8:N4}ms  p95={4,8:N4}ms  p99={5,8:N4}ms  max={6,8:N4}ms" -f `
        $Label, $Dist.Count, $Dist.Mean, $Dist.Median, $Dist.P95, $Dist.P99, $Dist.Max)
}

# ============================================================
# 4. ON/OFF 블록을 무작위로 인터리브해서 지연 분포를 모은다
# ============================================================

$blocksPerCondition = $Blocks / 2
$writeCountPerBlock = [int][Math]::Max(1, [Math]::Floor($Iterations / $blocksPerCondition))
$computeCountPerBlock = [int][Math]::Max(1, [Math]::Floor($ComputeIterations / $blocksPerCondition))

$labels = @()
for ($i = 0; $i -lt $blocksPerCondition; $i++) { $labels += 'OFF' }
for ($i = 0; $i -lt $blocksPerCondition; $i++) { $labels += 'ON' }
# Fisher-Yates가 아니라 무작위 키로 정렬 — 표본이 작을 때도(Blocks=8) 충분히
# 뒤섞이고, 코드가 짧아 실수할 여지가 적다.
$order = $labels | Sort-Object { Get-Random }

Write-Host "=== 지연 측정: 블록 순서(무작위) $($order -join ', ') ==="
Write-Host ("  블록당: 쓰기 {0}회, 연산 {1}회" -f $writeCountPerBlock, $computeCountPerBlock)
Write-Host ""

$offWrite = New-Object 'System.Collections.Generic.List[double]'
$onWrite  = New-Object 'System.Collections.Generic.List[double]'
$offCompute = New-Object 'System.Collections.Generic.List[double]'
$onCompute  = New-Object 'System.Collections.Generic.List[double]'

$offBlockWriteMedians = New-Object 'System.Collections.Generic.List[double]'
$onBlockWriteMedians  = New-Object 'System.Collections.Generic.List[double]'

$blockIndex = 0
foreach ($cond in $order) {
    $blockIndex++
    if ($cond -eq 'ON') {
        $proc = Start-OurPet -DataDir $dataDir
        $writeSamples = Measure-WriteLatencySamples -Path $transcriptPath -Count $writeCountPerBlock
        $computeSamples = Measure-ComputeLatencySamples -Count $computeCountPerBlock

        # 이 블록이 끝난 시점에도 펫이 여전히 살아 있는지 확인한다 — 조용히
        # 죽은 채로 "ON"이라고 재는 것을 막는다.
        $proc.Refresh()
        if ($proc.HasExited) {
            Write-Suspicious "블록 $blockIndex (ON) 도중 펫 프로세스가 사라졌습니다."
        }

        foreach ($s in $writeSamples) { $onWrite.Add($s) }
        foreach ($s in $computeSamples) { $onCompute.Add($s) }
        $onBlockWriteMedians.Add((Get-Distribution -Samples $writeSamples).Median)
    }
    else {
        Stop-OurPet
        $writeSamples = Measure-WriteLatencySamples -Path $transcriptPath -Count $writeCountPerBlock
        $computeSamples = Measure-ComputeLatencySamples -Count $computeCountPerBlock

        foreach ($s in $writeSamples) { $offWrite.Add($s) }
        foreach ($s in $computeSamples) { $offCompute.Add($s) }
        $offBlockWriteMedians.Add((Get-Distribution -Samples $writeSamples).Median)
    }
    Write-Host ("  블록 {0,2}/{1} [{2,-3}] 완료" -f $blockIndex, $Blocks, $cond)
}

Stop-OurPet

# 지연 측정용 세션을 여기서 바로 해제한다. 안 그러면 아래 CPU 구간 측정 동안
# 이 세션이 계속 등록된 채로 남아, 펫이 (활동 없는) 세션 하나를 더 추적하는
# 비용이 CPU 구간 측정에 섞여 들어간다 — 실제로는 세션이 하나뿐인 상황을
# 재는 것이 이 구간의 취지이므로, 미리 지운다.
Unregister-SyntheticSession -SessionFile $mainSessionFile
$mainSessionFile = $null

$offWriteDist = Get-Distribution -Samples $offWrite.ToArray()
$onWriteDist  = Get-Distribution -Samples $onWrite.ToArray()
$offComputeDist = Get-Distribution -Samples $offCompute.ToArray()
$onComputeDist  = Get-Distribution -Samples $onCompute.ToArray()

Write-Host ""
Write-Host "=== 파일 쓰기 지연 (같은 트랜스크립트 파일, ON에서는 펫이 동시에 tail 한다) ==="
Format-Distribution -Label "OFF (펫 없음)" -Dist $offWriteDist
Format-Distribution -Label "ON  (펫 실행 중)" -Dist $onWriteDist
$writeMedianDiff = $onWriteDist.Median - $offWriteDist.Median
$writeP99Diff = $onWriteDist.P99 - $offWriteDist.P99
Write-Host ("  중앙값 차이(ON-OFF): {0:N4} ms" -f $writeMedianDiff)
Write-Host ("  P99 차이(ON-OFF):    {0:N4} ms" -f $writeP99Diff)

# "재실행 시 부호가 뒤집힐 정도"를 블록 단위로도 확인한다: 조건 내부의
# 블록-대-블록 중앙값 변동폭이 조건 간 중앙값 차이보다 크거나 비슷하면,
# 그 차이는 노이즈 범위 안에 있다고 볼 수 있다.
$offBlockSpread = 0.0
$onBlockSpread = 0.0
if ($offBlockWriteMedians.Count -gt 1) {
    $offBlockSpread = ($offBlockWriteMedians | Measure-Object -Maximum).Maximum - ($offBlockWriteMedians | Measure-Object -Minimum).Minimum
}
if ($onBlockWriteMedians.Count -gt 1) {
    $onBlockSpread = ($onBlockWriteMedians | Measure-Object -Maximum).Maximum - ($onBlockWriteMedians | Measure-Object -Minimum).Minimum
}
Write-Host ("  블록간 중앙값 변동폭: OFF={0:N4}ms  ON={1:N4}ms  (참고: 조건 간 차이 {2:N4}ms와 비교)" -f `
    $offBlockSpread, $onBlockSpread, [Math]::Abs($writeMedianDiff))
Write-Host ""

Write-Host "=== CPU 바운드 연산 지연 (SHA-256, 트랜스크립트와 무관 — 두 번째 워크로드 축) ==="
Format-Distribution -Label "OFF (펫 없음)" -Dist $offComputeDist
Format-Distribution -Label "ON  (펫 실행 중)" -Dist $onComputeDist
$computeMedianDiff = $onComputeDist.Median - $offComputeDist.Median
$computeP99Diff = $onComputeDist.P99 - $offComputeDist.P99
Write-Host ("  중앙값 차이(ON-OFF): {0:N4} ms" -f $computeMedianDiff)
Write-Host ("  P99 차이(ON-OFF):    {0:N4} ms" -f $computeP99Diff)
Write-Host ""

# ============================================================
# 5. CPU 구간 측정 — 배회(wandering) / 잠듦(asleep) / 활동중(working)을
#    의도적으로 분리한다. 펫은 Idle 상태로 20초가 지나면 스스로 잠들어
#    렌더링을 완전히 멈춘다(PetWindow.Tick, SleepAfterTicks = 12fps*20초).
#    이를 무시하고 긴 구간을 통으로 재면, 배회 구간과 잠듦 구간이 뒤섞여
#    실제보다 낮은(더 보기 좋은) CPU 수치가 나올 수 있다 — 그래서 나눈다.
# ============================================================

function Measure-CpuPercent {
    param($Process, [int]$Seconds)
    $Process.Refresh()
    $cpuBefore = $Process.TotalProcessorTime
    $wallStart = Get-Date
    Start-Sleep -Seconds $Seconds
    $Process.Refresh()
    $cpuAfter = $Process.TotalProcessorTime
    $wallSeconds = ((Get-Date) - $wallStart).TotalSeconds
    $cpuSeconds = ($cpuAfter - $cpuBefore).TotalSeconds
    $perCore = ($cpuSeconds / $wallSeconds) * 100.0
    [pscustomobject]@{
        PerCorePercent    = $perCore
        SystemWidePercent = $perCore / $logicalProcessors
        CpuSeconds        = $cpuSeconds
        WallSeconds        = $wallSeconds
    }
}

function Measure-CpuPercentWithActivity {
    # 도구 이름을 하나로 고정하면(예: 항상 'Read') 상태 머신이 Reading 하나에
    # 고정돼 버려, 실제 세션의 "일하는 중" 활동을 대표하지 못한다 — Reading/
    # Writing은 제자리(반복 이동 없음)라 그 자체로 저비용일 수 있고, 그러면
    # "활동중" 구간이 우연히 최저비용 상태 하나만 재는 왜곡된 표본이 된다.
    # Read/Grep(Reading, 제자리), Edit(Writing, 제자리), Bash(분류 밖 → Running,
    # 이동)를 순환시켜 실제 세션에서 섞여 나오는 상태 전환을 흉내낸다.
    param($Process, [int]$Seconds, [string]$TranscriptPath, [int]$EventIntervalSeconds)
    $tools = @('Read', 'Edit', 'Bash', 'Grep')
    $toolIndex = 0
    $Process.Refresh()
    $cpuBefore = $Process.TotalProcessorTime
    $wallStart = Get-Date
    $elapsed = 0
    while ($elapsed -lt $Seconds) {
        Add-ToolUseLine -TranscriptPath $TranscriptPath -ToolName $tools[$toolIndex % $tools.Count]
        $toolIndex++
        $step = $EventIntervalSeconds
        if (($Seconds - $elapsed) -lt $step) { $step = $Seconds - $elapsed }
        Start-Sleep -Seconds $step
        $elapsed += $step
    }
    $Process.Refresh()
    $cpuAfter = $Process.TotalProcessorTime
    $wallSeconds = ((Get-Date) - $wallStart).TotalSeconds
    $cpuSeconds = ($cpuAfter - $cpuBefore).TotalSeconds
    $perCore = ($cpuSeconds / $wallSeconds) * 100.0
    [pscustomobject]@{
        PerCorePercent    = $perCore
        SystemWidePercent = $perCore / $logicalProcessors
        CpuSeconds        = $cpuSeconds
        WallSeconds        = $wallSeconds
    }
}

function Format-CpuResult {
    param([string]$Label, $Result)
    Write-Host ("  {0,-28} 코어당={1,7:N3}%   시스템전체={2,7:N4}%   (CPU {3:N3}s / 경과 {4:N2}s)" -f `
        $Label, $Result.PerCorePercent, $Result.SystemWidePercent, $Result.CpuSeconds, $Result.WallSeconds)
}

Write-Host "=== CPU 구간 측정 준비: 펫을 깨끗한 상태로 새로 기동 ==="
$cpuSessionId = "bench-cpu-" + [guid]::NewGuid().ToString('N')
$cpuSessionFile = Register-SyntheticSession -SessionId $cpuSessionId -TranscriptPath $cpuTranscriptPath
$cpuProc = Start-OurPet -DataDir $dataDir
Write-Host ("  PID {0}, 시작 시각 {1}" -f $cpuProc.Id, $cpuProc.StartTime)
# 추가 워밍업: 창 생성 직후에는 JIT·1회성 초기화 비용이 아직 남아있을 수
# 있다. 이걸 "배회" 표본에 섞으면 정상 상태(steady state)보다 높게 나온다.
# Start-OurPet의 1.5초 정착 대기에 이 2초를 더해, 배회 표본이 시작될 때는
# 이미 초기화가 끝난 정상 상태에 가깝도록 한다. (여전히 20초 잠듦 임계값
# 안에서 여유를 두고 계산한다 — 아래 배회 표본 종료 시점까지 총 유휴시간은
# 1.5 + 2 + WanderCpuSampleSeconds 초다.)
Start-Sleep -Seconds 2
Write-Host ""

Write-Host ("=== [1/3] 배회(Idle, 잠들기 전) — {0}초 표본, 잠듦 임계값(20초) 미만 ===" -f $WanderCpuSampleSeconds)
$wanderResult = Measure-CpuPercent -Process $cpuProc -Seconds $WanderCpuSampleSeconds
Format-CpuResult -Label "배회 CPU" -Result $wanderResult
$cpuProc.Refresh()
if ($cpuProc.HasExited) { Write-Suspicious "배회 구간 측정 중 펫이 사라졌습니다." }
Write-Host ""

Write-Host ("=== [2/3] 잠듦(Asleep) — 유휴 {0}초 추가 대기 뒤 {1}초 표본 ===" -f $SleepPreBufferSeconds, $SleepCpuSampleSeconds)
Start-Sleep -Seconds $SleepPreBufferSeconds
$sleepResult = Measure-CpuPercent -Process $cpuProc -Seconds $SleepCpuSampleSeconds
Format-CpuResult -Label "잠듦 CPU" -Result $sleepResult
$cpuProc.Refresh()
if ($cpuProc.HasExited) { Write-Suspicious "잠듦 구간 측정 중 펫이 사라졌습니다." }
Write-Host ""

Write-Host ("=== [3/3] 활동중(Working) — {0}초마다 tool_use 이벤트 주입, {1}초 표본 ===" -f $WorkingEventIntervalSeconds, $WorkingCpuSampleSeconds)
$workingResult = Measure-CpuPercentWithActivity -Process $cpuProc -Seconds $WorkingCpuSampleSeconds `
    -TranscriptPath $cpuTranscriptPath -EventIntervalSeconds $WorkingEventIntervalSeconds
Format-CpuResult -Label "활동중 CPU" -Result $workingResult
$cpuProc.Refresh()
if ($cpuProc.HasExited) { Write-Suspicious "활동중 구간 측정 중 펫이 사라졌습니다." }
Write-Host ""

# 배회/활동중이 잠듦보다 눈에 띄게 높지 않다면, 펫이 이미 잠들었거나(측정
# 실수) 렌더링을 안 하고 있다는 신호다 — 그대로 보고하지 않고 표시한다.
# (참고: 잠듦 CPU 자체가 사실상 0에 가까울 수 있어, 이 비율 검사는 절대적
# 하한이 아니라 "명백히 이상함"을 잡기 위한 느슨한 신호다.)
if ($wanderResult.PerCorePercent -le ($sleepResult.PerCorePercent * 1.5)) {
    Write-Suspicious ("배회 CPU({0:N3}%)가 잠듦 CPU({1:N3}%)보다 뚜렷하게 높지 않습니다. 펫이 실제로 애니메이션 중인지 재확인이 필요합니다." -f `
        $wanderResult.PerCorePercent, $sleepResult.PerCorePercent)
}
if ($workingResult.PerCorePercent -le ($sleepResult.PerCorePercent * 1.5)) {
    Write-Suspicious ("활동중 CPU({0:N3}%)가 잠듦 CPU({1:N3}%)보다 뚜렷하게 높지 않습니다. 주입한 트랜스크립트 이벤트가 실제로 반영되고 있는지 재확인이 필요합니다." -f `
        $workingResult.PerCorePercent, $sleepResult.PerCorePercent)
}

Unregister-SyntheticSession -SessionFile $cpuSessionFile
$cpuSessionFile = $null
Stop-OurPet

}
finally {
    # ============================================================
    # 6. 정리 — 예외가 나든 안 나든 항상 실행된다.
    # ============================================================
    if ($mainSessionFile) { Unregister-SyntheticSession -SessionFile $mainSessionFile }
    if ($cpuSessionFile)  { Unregister-SyntheticSession -SessionFile $cpuSessionFile }
    Stop-OurPet

    if ($KeepScratch) {
        Write-Host ("임시 디렉터리를 남겨둡니다 (-KeepScratch): {0}" -f $scratchRoot)
    }
    else {
        Remove-Item -Recurse -Force -Path $scratchRoot -ErrorAction SilentlyContinue
    }
}

# ============================================================
# 7. 요약
# ============================================================

Write-Host ""
Write-Host "=== 요약 ==="
Write-Host ("논리 프로세서 수: {0}" -f $logicalProcessors)
Write-Host ""
Write-Host "-- 파일 쓰기 지연 --"
Format-Distribution -Label "OFF" -Dist $offWriteDist
Format-Distribution -Label "ON"  -Dist $onWriteDist
Write-Host ("중앙값 차이: {0:N4} ms   P99 차이: {1:N4} ms" -f $writeMedianDiff, $writeP99Diff)
Write-Host ""
Write-Host "-- CPU 바운드 연산 지연 --"
Format-Distribution -Label "OFF" -Dist $offComputeDist
Format-Distribution -Label "ON"  -Dist $onComputeDist
Write-Host ("중앙값 차이: {0:N4} ms   P99 차이: {1:N4} ms" -f $computeMedianDiff, $computeP99Diff)
Write-Host ""
Write-Host "-- CPU 구간 (코어당 % / 시스템전체 %) --"
Format-CpuResult -Label "배회(wandering)" -Result $wanderResult
Format-CpuResult -Label "잠듦(asleep)"    -Result $sleepResult
Format-CpuResult -Label "활동중(working)" -Result $workingResult
Write-Host ""

if ($anySuspicious) {
    Write-Host "경고: 위에 [의심스러움]으로 표시된 항목이 있습니다. 결과를 그대로 신뢰하지 말고 원인을 조사하세요." -ForegroundColor Yellow
}
else {
    Write-Host "이상 신호 없음: 펫은 각 측정 구간 내내 살아 있었고, 배회/활동중 CPU가 잠듦 CPU보다 뚜렷하게 높았습니다."
}
