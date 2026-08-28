using PetCore;
using Xunit;

public class PetStateTests
{
    [Fact]
    public void PetState_HasAllSixStates()
    {
        Assert.Equal(6, Enum.GetValues<PetState>().Length);
        Assert.True(Enum.IsDefined(PetState.NeedsYou));
    }
}
