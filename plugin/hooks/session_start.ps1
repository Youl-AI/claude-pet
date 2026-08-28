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
    # 이 탐색은 실패할 수 있다(WMI 비활성화, 권한 문제, 6홉 안에 못 찾음 등).
    # 탐색 실패가 세션 기록 자체를 막아서는 안 된다 — sessionId/transcriptPath가
    # 없는 것이 pid=0인 것보다 훨씬 나쁘다(펫이 이 세션의 트랜스크립트를 아예
    # 추적하지 못한다). 그래서 이 블록만 별도로 감싼다.
    $pidValue = 0
    $startMs = 0
    try {
        $current = Get-CimInstance Win32_Process -Filter "ProcessId=$PID" -ErrorAction SilentlyContinue
        for ($i = 0; $i -lt 6 -and $current; $i++) {
            if ($current.Name -match '^(claude|node)') {
                $pidValue = [int]$current.ProcessId
                $startMs = [DateTimeOffset]::new($current.CreationDate.ToUniversalTime(), [TimeSpan]::Zero).ToUnixTimeMilliseconds()
                break
            }
            $current = Get-CimInstance Win32_Process -Filter "ProcessId=$($current.ParentProcessId)" -ErrorAction SilentlyContinue
        }
    }
    catch {
        # pid=0으로 남는다. 워치독은 이 세션을 "생존 확인 불가"로 보고
        # grace 시간 뒤에 펫을 닫는다 — 세션을 방해하는 것보다 이르게 닫히는 편이 낫다.
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
