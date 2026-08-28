# 펫 크래시 복구 공용 헬퍼. session_start.ps1 / notification.ps1 이 dot-source 해서 쓴다.
# 이 파일 자체는 실행 진입점이 아니다 — exit 0 계약은 이 파일을 불러오는 훅 스크립트의
# try/catch가 책임진다. 즉 이 파일 안에서 예기치 못한 예외가 던져져도(OpenExisting의
# 알려지지 않은 실패 등) 훅 쪽 catch가 삼키고 exit 0으로 끝난다 — 여기서 전부 다시
# try/catch로 감쌀 필요는 없다.

# 펫이 살아있는지는 프로세스 이름이 아니라 뮤텍스로 판정한다.
# 이 머신에는 VS Code Python 확장이 띄우는 무관한 pet.exe 도 돌고 있어서
# 이름 매칭은 오탐을 낸다. 뮤텍스(Local\claude-pet)는 펫 자신이 SingleInstance.TryAcquire
# 에서 쓰는 것과 동일한 권위이므로 이것으로 판정하는 게 맞다.
function Test-PetRunning {
    try {
        $m = [System.Threading.Mutex]::OpenExisting('Local\claude-pet')
        # 열기만 하고 즉시 놓아준다 — 우리는 소유권을 원하는 게 아니라 존재 여부만 알고 싶다.
        $m.Dispose()
        return $true
    }
    catch [System.Threading.WaitHandleCannotBeOpenedException] {
        # 이름의 뮤텍스가 없다 = 펫이 떠 있지 않다. 이 경우만 여기서 삼킨다.
        return $false
    }
}

# 펫이 죽어 있으면 다시 띄운다. 세션당 최대 4번(카운터가 3을 초과하기 전까지)까지만
# 시도하는 서킷 브레이커를 둔다 — pet.exe 가 뜨자마자 죽는 상황(예: 손상된 빌드)에서
# 매 알림마다 무한히 Start-Process 를 시도하는 것을 막기 위해서다.
function Invoke-PetRecovery {
    param(
        [Parameter(Mandatory = $false)] [string] $SessionId,
        [Parameter(Mandatory = $false)] [string] $DataDir,
        [Parameter(Mandatory = $false)] [string] $PluginRoot
    )

    if ([string]::IsNullOrWhiteSpace($SessionId)) { return }
    if ([string]::IsNullOrWhiteSpace($DataDir)) { return }

    # 1) 먼저 생존을 확인한다. 살아 있으면 여기서 끝 — 카운터는 절대 건드리지 않는다.
    #    순서가 중요한 이유: 카운터는 "펫이 없어서 다시 띄운 횟수"를 재는 것이지
    #    "알림이 온 횟수"를 재는 게 아니다. 만약 카운터 증가를 먼저(또는 생존 확인과
    #    무관하게) 했다면, 펫이 멀쩡히 떠 있는 건강한 세션에서도 알림이 4번만 오면
    #    서킷 브레이커가 끊겨버린다 — 정작 필요할 때(진짜로 죽었을 때) 복구를 못 하게
    #    되는 정반대의 결과를 낳는다. 그래서 "죽어 있는 게 확인된 경우에만" 카운터를 센다.
    if (Test-PetRunning) { return }

    # 2) 서킷 브레이커. 세션별 카운터 파일로 시도 횟수를 추적한다.
    $launchDir = Join-Path $DataDir 'launch'
    New-Item -ItemType Directory -Force -Path $launchDir | Out-Null
    $counterFile = Join-Path $launchDir "$SessionId.count"

    [int]$count = 0
    if (Test-Path $counterFile) {
        $raw = Get-Content -Path $counterFile -Raw -ErrorAction SilentlyContinue
        if ($raw) {
            $parsed = 0
            if ([int]::TryParse($raw.Trim(), [ref]$parsed)) { $count = $parsed }
        }
    }

    if ($count -gt 3) {
        # 이미 이 세션에서 4번 시도했다. 더 이상 시도하지 않는다.
        return
    }

    $count++
    $temp = "$counterFile.tmp"
    Set-Content -Path $temp -Value $count -Encoding utf8
    Move-Item -Force -Path $temp -Destination $counterFile

    # 3) 실행. session_start.ps1이 원래 쓰던 것과 동일한 호출 — 대기하지 않고, 숨겨서 띄운다.
    if ([string]::IsNullOrWhiteSpace($PluginRoot)) { return }
    $exe = Join-Path $PluginRoot 'bin/pet.exe'
    if (Test-Path $exe) {
        Start-Process -FilePath $exe -WindowStyle Hidden -ErrorAction SilentlyContinue
    }
}
