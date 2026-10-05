using Industrial.Diagnostics.Worker.Features.Diagnostics.ML;

namespace Industrial.Diagnostics.Tests;

public sealed class ModelEngineTests
{
    [Fact]
    public async Task Equipment_states_are_isolated_cached_and_rebuilt_after_invalidation()
    {
        using var engine = new ModelEngine(Path.Combine(AppContext.BaseDirectory, "model.zip"));
        var aLoads = 0;
        var bLoads = 0;

        Task<IReadOnlyList<float>> LoadA(CancellationToken _)
        {
            aLoads++;
            return Task.FromResult<IReadOnlyList<float>>([90, 91, 89]);
        }

        Task<IReadOnlyList<float>> LoadB(CancellationToken _)
        {
            bLoads++;
            return Task.FromResult<IReadOnlyList<float>>([70, 71, 69]);
        }

        var firstA = await engine.InspectAsync("EQ-A", 92, LoadA, CancellationToken.None);
        var secondA = await engine.InspectAsync("EQ-A", 93, LoadA, CancellationToken.None);
        await engine.InspectAsync("EQ-B", 72, LoadB, CancellationToken.None);

        Assert.Equal(1, aLoads);
        Assert.Equal(1, bLoads);
        Assert.Equal(3, firstA.HistoryCount);
        Assert.Equal(4, secondA.HistoryCount);
        Assert.Equal(64, firstA.DetectorVersion.Length);
        Assert.Equal(engine.DetectorVersion, firstA.DetectorVersion);

        engine.Invalidate("EQ-A");
        var rebuiltA = await engine.InspectAsync("EQ-A", 94, LoadA, CancellationToken.None);

        Assert.Equal(2, aLoads);
        Assert.Equal(1, bLoads);
        Assert.Equal(3, rebuiltA.HistoryCount);
    }

    [Fact]
    public async Task Implausible_readings_are_range_gated_without_touching_detector_state()
    {
        using var engine = new ModelEngine(Path.Combine(AppContext.BaseDirectory, "model.zip"));
        Task<IReadOnlyList<float>> Load(CancellationToken _) =>
            Task.FromResult<IReadOnlyList<float>>([90, -999, 91, 89]);

        var first = await engine.InspectAsync("EQ-A", 92, Load, CancellationToken.None);
        var dropout = await engine.InspectAsync("EQ-A", -999, Load, CancellationToken.None);
        var next = await engine.InspectAsync("EQ-A", 93, Load, CancellationToken.None);

        // The -999 in the warm-up history is skipped, so three plausible readings warm the state.
        Assert.Equal(3, first.HistoryCount);
        Assert.Equal(ModelEngine.IidSpikeMethod, first.Method);

        Assert.True(dropout.IsAnomaly);
        Assert.Equal(ModelEngine.RangeGateMethod, dropout.Method);
        Assert.Equal(0, dropout.PValue);
        Assert.Equal(4, dropout.HistoryCount);

        // The gated reading did not advance the window.
        Assert.Equal(4, next.HistoryCount);
        Assert.Equal(ModelEngine.IidSpikeMethod, next.Method);
    }
}
