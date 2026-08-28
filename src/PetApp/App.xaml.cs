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

        // 뮤텍스 스레드 소속: SingleInstance.TryAcquire (여기, OnStartup)와
        // _instance.Dispose (OnExit)가 서로 다른 스레드에서 불리면 Mutex.ReleaseMutex가
        // ApplicationException을 던진다. 이 앱은 그럴 수 없다 — App.g.cs의 생성된
        // Main()이 [STAThread]이고 `app.Run()`을 동기 호출 한 번으로 끝내는 단일
        // WPF 진입점이며, 별도 스레드나 별도 Dispatcher를 만드는 코드가 이 프로젝트
        // 어디에도 없다. OnStartup은 그 Run() 호출 스택 안에서 바로 실행되고, OnExit는
        // Shutdown()이 같은 스레드의 Dispatcher 메시지 루프를 통해 처리될 때 실행된다
        // (PetHost.Poll이 부르는 Application.Current.Shutdown()도 DispatcherTimer.Tick
        // 콜백 — 즉 이 스레드의 Dispatcher 큐 — 안에서만 호출된다). 그래서 획득과 해제는
        // 항상 이 프로세스의 유일한 STA 메인 스레드에서 일어난다. 별도의 런타임 검증
        // 코드는 추가하지 않는다 — 이 불변식을 깨려면 Dispatcher를 새로 만들거나
        // 백그라운드 스레드에서 Shutdown을 부르는 코드가 필요한데, 그런 코드는 존재하지
        // 않는다.
        //
        // 펫은 한 마리. SessionStart 는 startup/resume/clear/compact/fork 에서 모두 발생한다.
        if (!SingleInstance.TryAcquire("claude-pet", out _instance))
        {
            Shutdown();
            return;
        }

        // 빌드·테스트와 CPU를 두고 경쟁하지 않는다 (설계서 §6.5).
        // catch(Exception)으로 넓게 잡는다 — 여기서 나올 수 있는 예외를 미리 다 나열할
        // 수 없다(프로세스가 이미 종료된 InvalidOperationException은 물론, 권한 문제로
        // 인한 Win32Exception도 있다 — 이 프로젝트는 "IOException만 잡으면 된다"는
        // 가정이 세 번 깨진 이력이 있다). 우선순위 설정 실패가 펫 기동 자체를 막아서는
        // 안 되므로, 실패는 조용히 무시하고 계속 진행한다.
        try
        {
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception)
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
