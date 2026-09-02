using Industrial.Sensor.EdgeGateway.Features.Buffer;

namespace Industrial.EdgeGateway.Tests;

public sealed class BufferDepthTests
{
    [Fact]
    public void TryReserve_enforces_the_exact_ceiling_under_concurrency()
    {
        const int capacity = 128;
        var depth = new BufferDepth();
        var accepted = 0;

        Parallel.For(
            0,
            10_000,
            _ =>
            {
                if (depth.TryReserve(capacity))
                    Interlocked.Increment(ref accepted);
            }
        );

        Assert.Equal(capacity, accepted);
        Assert.Equal(capacity, depth.Current);
        Assert.False(depth.TryReserve(capacity));

        depth.Release(capacity);
        Assert.Equal(0, depth.Current);
        Assert.True(depth.TryReserve(capacity));
    }

    [Fact]
    public void Initialize_restores_the_durable_startup_count()
    {
        var depth = new BufferDepth();

        depth.Initialize(42);

        Assert.Equal(42, depth.Current);
    }

    [Fact]
    public void Invalid_release_does_not_corrupt_the_counter()
    {
        var depth = new BufferDepth();
        depth.Initialize(2);

        Assert.Throws<InvalidOperationException>(() => depth.Release(3));
        Assert.Equal(2, depth.Current);
    }
}
