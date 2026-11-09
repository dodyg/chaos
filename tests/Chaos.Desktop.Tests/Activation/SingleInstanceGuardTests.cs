using Chaos.Desktop.Windows.Activation;

namespace Chaos.Desktop.Tests.Activation;

public sealed class SingleInstanceGuardTests : IDisposable
{
    private readonly string _name = "test-" + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        // Named mutexes for this test's unique name are released by the guards themselves.
    }

    [Test]
    public async Task Acquire_FirstInstance_IsPrimary()
    {
        using var guard = new SingleInstanceGuard(_name);

        await Assert.That(guard.IsPrimaryInstance).IsTrue();
    }

    [Test]
    public async Task Acquire_SecondInstance_IsNotPrimary()
    {
        using var primary = new SingleInstanceGuard(_name);

        // Mutex ownership is thread-affine (reentrant for the owning thread). A real
        // second instance runs in another process, so the probe must run on a different
        // thread to observe the held lock.
        SingleInstanceGuard? secondary = null;
        await Task.Run(() =>
        {
            secondary = new SingleInstanceGuard(_name);
            return secondary.IsPrimaryInstance;
        });

        using (secondary)
        {
            await Assert.That(primary.IsPrimaryInstance).IsTrue();
            await Assert.That(secondary!.IsPrimaryInstance).IsFalse();
        }
    }

    [Test]
    public async Task Acquire_AfterPrimaryReleased_LockIsAvailableAgain()
    {
        using (var primary = new SingleInstanceGuard(_name))
        {
            await Assert.That(primary.IsPrimaryInstance).IsTrue();
        }

        using var next = new SingleInstanceGuard(_name);

        await Assert.That(next.IsPrimaryInstance).IsTrue();
    }
}
