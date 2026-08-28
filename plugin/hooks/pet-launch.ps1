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

# WPF 앱은 Microsoft.WindowsDesktop.App 프레임워크를 요구한다. 그게 없으면 apphost가
# 오류 대화상자를 띄우는데, Start-Process -WindowStyle Hidden 이 STARTUPINFO 의
# wShowWindow=SW_HIDE 를 넘기는 탓에 그 대화상자(#32770)가 "숨겨진 채로" 뜬다.
# 실측 결과: 사용자 눈에는 아무것도 안 보이는데 프로세스는 모달 대화상자에 걸려
# 15초가 지나도 종료되지 않았고, 그 창이 포그라운드를 가져가기까지 했다.
# 게다가 .NET 코드에 진입조차 못 하므로 Local\claude-pet 뮤텍스를 만들지 않고,
# 그래서 Test-PetRunning 이 영원히 "죽었다"로 판정해 알림이 올 때마다 보이지 않는
# 좀비가 하나씩 쌓인다.
#
# 그래서 아예 띄우기 전에 런타임 존재를 확인한다. 확인이 실패하면 "있다"로 본다 —
# 확인 실패 때문에 멀쩡한 환경에서 펫이 안 뜨는 쪽이 더 나쁘고, 설령 잘못 띄워도
# 호출부가 DOTNET_DISABLE_GUI_ERRORS=1 을 걸어 두어 대화상자 대신 즉시 종료된다.
function Test-DesktopRuntime {
    param([int] $MinimumMajor = 10)
    try {
        $roots = @()
        if ($env:DOTNET_ROOT) { $roots += $env:DOTNET_ROOT }
        if ($env:ProgramFiles) { $roots += (Join-Path $env:ProgramFiles 'dotnet') }
        foreach ($root in $roots) {
            $dir = Join-Path $root 'shared\Microsoft.WindowsDesktop.App'
            if (-not (Test-Path $dir)) { continue }
            foreach ($v in (Get-ChildItem -Path $dir -Directory -ErrorAction SilentlyContinue)) {
                $major = ($v.Name -split '\.')[0]
                if ($major -match '^\d+$' -and [int]$major -ge $MinimumMajor) { return $true }
            }
        }
        return $false
    }
    catch {
        return $true
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
    if ([string]::IsNullOrWhiteSpace($PluginRoot)) { return }

    # 1) 먼저 생존을 확인한다. 살아 있으면 여기서 끝 — 카운터는 절대 건드리지 않는다.
    #    순서가 중요한 이유: 카운터는 "펫이 없어서 다시 띄운 횟수"를 재는 것이지
    #    "알림이 온 횟수"를 재는 게 아니다. 만약 카운터 증가를 먼저(또는 생존 확인과
    #    무관하게) 했다면, 펫이 멀쩡히 떠 있는 건강한 세션에서도 알림이 4번만 오면
    #    서킷 브레이커가 끊겨버린다 — 정작 필요할 때(진짜로 죽었을 때) 복구를 못 하게
    #    되는 정반대의 결과를 낳는다. 그래서 "죽어 있는 게 확인된 경우에만" 카운터를 센다.
    if (Test-PetRunning) { return }

    # 2) 애초에 띄울 수 있는 환경인지 먼저 본다 — 서킷 브레이커 카운터를 태우기 전에.
    #    카운터가 세려는 것은 "띄웠는데 죽더라"이지 "띄울 수 없는 환경"이 아니다.
    #    (예전에는 이 두 검사가 카운터 증가 뒤에 있어서, 실행 파일이 없는 설치에서도
    #     시도 횟수만 4까지 올라가고 launch/*.count 파일이 남았다.)
    $exe = Join-Path $PluginRoot 'bin/pet.exe'
    if (-not (Test-Path $exe)) { return }
    if (-not (Test-DesktopRuntime)) { return }

    # 3) 서킷 브레이커. 세션별 카운터 파일로 시도 횟수를 추적한다.
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

    # 4) 실행. 대기하지 않고, 숨겨서 띄운다.
    #
    #    -WorkingDirectory: 이걸 주지 않으면 자식은 훅 프로세스의 CWD, 즉 사용자의
    #    프로젝트 폴더를 물려받는다. 프로세스의 CWD 는 그 폴더에 대한 열린 핸들이라
    #    사용자가 그 폴더를 지우거나 이름을 바꾸거나 옮길 수 없게 된다(실측 확인:
    #    "The process cannot access the file because it is being used by another
    #    process"). 펫은 한 마리뿐이라 처음 띄운 세션의 폴더를 계속 붙잡고 있고,
    #    작업 표시줄에도 Alt+Tab 에도 안 보여서 사용자가 원인을 찾을 수 없다.
    #    플러그인 자기 폴더를 물게 해서 사용자 작업물에서 손을 뗀다.
    #
    #    DOTNET_DISABLE_GUI_ERRORS: 위 Test-DesktopRuntime 를 통과했더라도(예: 확인이
    #    예외로 실패해 $true 를 돌려준 경우) 런타임이 실제로는 없을 수 있다. 이 변수가
    #    걸려 있으면 apphost 는 숨겨진 모달 대화상자 대신 즉시 종료한다 — 좀비가 남지
    #    않는다. Start-Process 에 환경변수 파라미터가 없으므로 자식이 상속하도록
    #    잠깐 설정했다가 되돌린다.
    $previousGuiErrors = $env:DOTNET_DISABLE_GUI_ERRORS
    $env:DOTNET_DISABLE_GUI_ERRORS = '1'
    try {
        Start-Process -FilePath $exe `
                      -WorkingDirectory $PluginRoot `
                      -WindowStyle Hidden `
                      -ErrorAction SilentlyContinue
    }
    finally {
        $env:DOTNET_DISABLE_GUI_ERRORS = $previousGuiErrors
    }
}
