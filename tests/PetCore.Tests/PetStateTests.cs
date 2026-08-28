using PetCore;
using Xunit;

public class PetStateTests
{
    [Fact]
    public void PetState_HasAllEightStates()
    {
        // 스프라이트 시트는 상태당 한 행이고 SpriteSheet 생성자가 이 개수와
        // 행 수가 어긋나면 던진다. 상태를 추가하면 pet.png 도 함께 갱신해야 한다.
        Assert.Equal(8, Enum.GetValues<PetState>().Length);
    }

    [Fact]
    public void PetState_SeparatesTheThreeWaitingSituations()
    {
        // 예전에는 셋이 NeedsYou 하나로 뭉쳐 있어 렌더러가 구분할 수 없었다.
        Assert.True(Enum.IsDefined(PetState.YourTurn));
        Assert.True(Enum.IsDefined(PetState.Blocked));
        Assert.True(Enum.IsDefined(PetState.Abandoned));
    }
}
