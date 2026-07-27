using MHC.Invoicing.Application.Runtime;

namespace MHC.Invoicing.Application.Tests.Runtime;

public sealed class SingleInstanceLeaseTests
{
    [Fact]
    public void TryAcquire_WhenAnotherThreadOwnsName_ReturnsFalseUntilReleased()
    {
        string name = $"mhc-invoicing-test-{Guid.NewGuid():N}";
        Assert.True(SingleInstanceLease.TryAcquire(name, out SingleInstanceLease? first));
        Assert.NotNull(first);
        try
        {
            bool acquiredWhileHeld = true;
            Thread contender = new(() =>
            {
                acquiredWhileHeld = SingleInstanceLease.TryAcquire(name, out SingleInstanceLease? second);
                second?.Dispose();
            });
            contender.Start();
            contender.Join();

            Assert.False(acquiredWhileHeld);
        }
        finally
        {
            first.Dispose();
        }

        Assert.True(SingleInstanceLease.TryAcquire(name, out SingleInstanceLease? afterRelease));
        afterRelease?.Dispose();
    }

    [Fact]
    public void TryAcquire_RejectsBlankName()
    {
        Assert.Throws<ArgumentException>(() => SingleInstanceLease.TryAcquire(" ", out _));
    }
}
