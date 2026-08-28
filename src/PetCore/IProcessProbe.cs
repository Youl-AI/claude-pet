namespace PetCore;

public interface IProcessProbe
{
    /// <summary>
    /// PID 재사용을 막기 위해 시작 시각까지 대조한다.
    /// startUnixMs 가 0 이면 시작 시각 대조를 생략한다.
    /// </summary>
    bool IsAlive(int pid, long startUnixMs);
}
