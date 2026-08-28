using System.Threading;

namespace PetCore;

public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private bool _disposed;

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>
    /// 다른 프로세스가 이미 같은 이름으로 인스턴스를 보유 중이면 false를 반환한다(블로킹 없음, 타임아웃 0).
    /// </summary>
    /// <remarks>
    /// 주의: Windows Mutex는 이를 소유한 스레드에 대해 재진입(reentrant)이 가능하다.
    /// 즉 같은 스레드에서 두 번째로 TryAcquire를 호출하면(또는 WaitOne을 다시 호출하면) 성공한다 —
    /// 이는 버그가 아니라 뮤텍스의 정상 동작이다. 이 가드가 막으려는 것은 "같은 스레드의 재호출"이
    /// 아니라 "다른 프로세스의 동시 실행"이므로, 상호 배제를 검증하는 테스트는 반드시 두 번째
    /// 획득 시도를 별도의 스레드(이상적으로는 별도 프로세스를 흉내낼 수 있는 스레드)에서 수행해야 한다.
    /// </remarks>
    public static bool TryAcquire(string name, out SingleInstance? instance)
    {
        // Local\ : 이 로그인 세션(현재 사용자 세션)으로 범위를 한정한다.
        // Global\ 을 쓰면 뮤텍스가 머신 전체에서 공유되어, 빠른 사용자 전환 등으로
        // 같은 컴퓨터에 두 명이 동시에 로그인한 경우 한쪽 세션이 펫을 아예 못 띄우게 된다.
        // 데스크톱 펫은 세션 단위 앱이므로 Local\ 이 맞는 범위다.
        var mutex = new Mutex(initiallyOwned: false, $"Local\\{name}");
        bool acquired;
        try
        {
            acquired = mutex.WaitOne(TimeSpan.Zero, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            // 이전 소유자가 죽었다. 우리가 가져간다.
            acquired = true;
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
