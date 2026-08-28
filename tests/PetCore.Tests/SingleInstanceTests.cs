using System.Threading;
using PetCore;
using Xunit;

public class SingleInstanceTests
{
    [Fact]
    public void FirstAcquireSucceeds_SecondFails_ThirdSucceedsAfterRelease()
    {
        var name = $"claude-pet-test-{Guid.NewGuid():N}";

        // Phase 1: first acquire succeeds (on the test thread).
        Assert.True(SingleInstance.TryAcquire(name, out var first));
        Assert.NotNull(first);

        // Phase 2: a Windows mutex is reentrant for the thread that already owns it,
        // so a second TryAcquire from THIS thread would succeed and prove nothing.
        // Attempt it from a different thread instead, so the OS actually enforces
        // mutual exclusion against a non-owning thread (the same way a second
        // process would be denied).
        bool secondAcquired = true;
        SingleInstance? second = null;
        var acquireThread = new Thread(() =>
        {
            secondAcquired = SingleInstance.TryAcquire(name, out second);
        });
        acquireThread.Start();
        acquireThread.Join();

        Assert.False(secondAcquired);
        Assert.Null(second);

        first!.Dispose();

        // Phase 3: after release, an acquire from another thread succeeds again.
        bool thirdAcquired = false;
        SingleInstance? third = null;
        var reacquireThread = new Thread(() =>
        {
            thirdAcquired = SingleInstance.TryAcquire(name, out third);
        });
        reacquireThread.Start();
        reacquireThread.Join();

        Assert.True(thirdAcquired);
        Assert.NotNull(third);
        third!.Dispose();
    }
}
