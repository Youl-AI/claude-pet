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
        catch (Exception)
        {
            // 살아있다는 확증이 없으면 죽은 것으로 본다.
            //
            // catch-all은 의도적이다. 이 메서드는 예외를 던져서는 안 되고,
            // 예외 종류를 하나씩 열거하는 방식은 이 프로젝트에서 이미 세 번 실패했다.
            // 실제로 나올 수 있는 것만 해도: 프로세스가 없으면 ArgumentException,
            // 조회 도중 종료되면 InvalidOperationException, 권한이 없는(예: 상승된)
            // 프로세스의 StartTime을 읽으면 Win32Exception — 이 중 마지막은
            // IOException 계열도 SystemException 계열도 아니다.
            //
            // 오탐(살아있는데 죽었다고 판단)의 대가는 워치독이 펫을 조금 일찍 닫는 것뿐이고,
            // 미탐의 대가는 좀비 프로세스가 남는 것이다. 전자가 낫다.
            return false;
        }
    }
}
