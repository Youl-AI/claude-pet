using PetCore;
using Xunit;

public class SingleInstanceTests
{
    [Fact]
    public void FirstAcquireSucceeds_SecondFails_ThirdSucceedsAfterRelease()
    {
        var name = $"claude-pet-test-{Guid.NewGuid():N}";

        Assert.True(SingleInstance.TryAcquire(name, out var first));
        Assert.NotNull(first);

        Assert.False(SingleInstance.TryAcquire(name, out var second));
        Assert.Null(second);

        first!.Dispose();

        Assert.True(SingleInstance.TryAcquire(name, out var third));
        third!.Dispose();
    }
}
