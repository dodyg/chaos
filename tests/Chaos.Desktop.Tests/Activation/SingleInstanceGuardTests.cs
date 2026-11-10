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
        // Both the owner and probe use dedicated threads; mutex ownership is thread-affine.
        var result = new TaskCompletionSource<(bool Primary, bool Secondary)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new Thread(() =>
        {
            try
            {
                using var primary = new SingleInstanceGuard(_name);
                var isSecondaryPrimary = false;
                var probe = new Thread(() => { using var secondary = new SingleInstanceGuard(_name); isSecondaryPrimary = secondary.IsPrimaryInstance; });
                probe.Start();
                probe.Join();
                result.SetResult((primary.IsPrimaryInstance, isSecondaryPrimary));
            }
            catch (Exception error) { result.SetException(error); }
        });
        owner.Start();
        var observed = await result.Task;
        owner.Join();
        await Assert.That(observed.Primary).IsTrue();
        await Assert.That(observed.Secondary).IsFalse();
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
