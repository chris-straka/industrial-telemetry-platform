using System.Diagnostics;

using Industrial.Diagnostics.Worker.Features.Diagnostics.ML;

using Xunit.Abstractions;

namespace Industrial.Diagnostics.Tests;

/// <summary>
/// Replays the sensor emulator's fault model through the production detector artifact and prints
/// recall, false-positive rate, and in-process throughput. Kafka, Postgres, and warm-up queries are
/// excluded, so throughput is a ceiling for the detector alone, not an end-to-end figure.
/// </summary>
/// <remarks>
/// The fault model mirrors <c>Industrial.Sensor.Emulator/Program.cs</c>: every 10th reading is a
/// -999 C dropout, every 7th otherwise is a 245 C overheat, and the rest are uniform in
/// [70, 110). A fixed seed keeps the printed numbers reproducible.
/// </remarks>
public sealed class DetectorQualityTests(ITestOutputHelper output)
{
    private const int Devices = 8;
    private const int ReadingsPerDevice = 700;

    [Fact]
    public async Task Emulator_fault_model_recall_false_positives_and_throughput()
    {
        using var engine = new ModelEngine(Path.Combine(AppContext.BaseDirectory, "model.zip"));
        var random = new Random(20261005);
        static Task<IReadOnlyList<float>> NoHistory(CancellationToken _) =>
            Task.FromResult<IReadOnlyList<float>>([]);

        int overheats = 0, overheatsFlagged = 0;
        int dropouts = 0, dropoutsFlagged = 0;
        int normals = 0, normalsFlagged = 0;
        var inspections = 0;

        var stopwatch = Stopwatch.StartNew();
        for (long seq = 1; seq <= ReadingsPerDevice; seq++)
        {
            for (var device = 0; device < Devices; device++)
            {
                var isDropout = seq % 10 == 0;
                var isOverheat = !isDropout && seq % 7 == 0;
                var temperature =
                    isDropout ? -999f
                    : isOverheat ? 245f
                    : (float)(random.NextDouble() * 40 + 70);

                var result = await engine.InspectAsync(
                    $"EQ-{device}",
                    temperature,
                    NoHistory,
                    CancellationToken.None
                );
                inspections++;

                // The first window is warm-up: the detector has no reference yet.
                if (seq <= ModelEngine.HistoryLength)
                    continue;

                if (isDropout)
                {
                    dropouts++;
                    dropoutsFlagged += result.IsAnomaly ? 1 : 0;
                }
                else if (isOverheat)
                {
                    overheats++;
                    overheatsFlagged += result.IsAnomaly ? 1 : 0;
                }
                else
                {
                    normals++;
                    normalsFlagged += result.IsAnomaly ? 1 : 0;
                }
            }
        }
        stopwatch.Stop();

        var overheatRecall = (double)overheatsFlagged / overheats;
        var dropoutRecall = (double)dropoutsFlagged / dropouts;
        var falsePositiveRate = (double)normalsFlagged / normals;

        output.WriteLine(
            $"detector: {inspections} inspections over {Devices} devices in "
                + $"{stopwatch.Elapsed.TotalSeconds:F2}s = {inspections / stopwatch.Elapsed.TotalSeconds:F0} inspections/s"
        );
        output.WriteLine(
            $"overheat recall {overheatRecall:P1} ({overheatsFlagged}/{overheats}), "
                + $"dropout recall {dropoutRecall:P1} ({dropoutsFlagged}/{dropouts}), "
                + $"false-positive rate {falsePositiveRate:P1} ({normalsFlagged}/{normals})"
        );

        // Loose guards: they fail if the artifact or engine regresses badly, not on noise.
        Assert.True(overheatRecall >= 0.9, $"overheat recall {overheatRecall:P1}");
        Assert.True(dropoutRecall >= 0.9, $"dropout recall {dropoutRecall:P1}");
        Assert.True(falsePositiveRate <= 0.25, $"false-positive rate {falsePositiveRate:P1}");
    }
}
