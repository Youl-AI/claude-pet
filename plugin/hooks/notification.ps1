# 사람이 필요한 순간을 펫에게 알린다.
# 이 훅은 사용자가 코딩하고 있지 않을 때만 발생한다.
try {
    $ErrorActionPreference = 'Stop'
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

    # 알림이 왔다는 건 사람이 필요하다는 뜻 — 죽은 펫이 가장 문제가 되는 순간이다.
    # 그래서 여기서도 복구를 시도한다(세션 시작 이후 크래시한 펫을 여기서 되살린다).
    . (Join-Path $PSScriptRoot 'pet-launch.ps1')
    Invoke-PetRecovery -SessionId $payload.session_id -DataDir $dataDir -PluginRoot $env:CLAUDE_PLUGIN_ROOT
}
catch { }
exit 0
