# 세션 등록을 해제한다. 이것은 빠른 길일 뿐이며 권위가 아니다.
# 실패해도 펫의 PID 워치독이 정리한다.
try {
    $ErrorActionPreference = 'Stop'
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
