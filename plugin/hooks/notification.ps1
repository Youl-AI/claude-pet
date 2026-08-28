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
