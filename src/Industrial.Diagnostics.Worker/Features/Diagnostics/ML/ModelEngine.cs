using System.Collections.Concurrent;
using System.Security.Cryptography;

using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Transforms.TimeSeries;

namespace Industrial.Diagnostics.Worker.Features.Diagnostics.ML;

public class TelemetryData
{
    [LoadColumn(2)]
    public float EngineTemperature { get; set; }
}

public class AnomalyPrediction
{
    // IID spike detection always returns [alert, raw score, p-value].
    [VectorType(3)]
    public double[] Prediction { get; set; } = default!;
}

/// <summary>One scored reading plus the provenance persisted beside it.</summary>
/// <remarks>
/// <c>Method</c> is <see cref="ModelEngine.IidSpikeMethod"/> when the online detector scored the
/// reading, or <see cref="ModelEngine.RangeGateMethod"/> when it was physically implausible and
/// never reached the detector. A range-gated p-value of 0 is a rule's certainty, not a
/// statistical result.
/// </remarks>
public record MachineHealthResult(
    bool IsAnomaly,
    double Score,
    double PValue,
    int HistoryCount,
    string DetectorVersion,
    string Method
);

/// <summary>
/// Owns one online IID detector state per equipment identity.
/// </summary>
/// <remarks>
/// model.zip stores the detector configuration and schema; IID spike detection learns its
/// reference window online rather than fitting parameters from a training set. Mixing equipment
/// in one prediction engine makes one machine's temperature become another machine's history, so
/// each key gets an isolated engine. A newly created state is warmed from persisted readings,
/// which makes restarts and Kafka rebalances converge on the same recent history.
///
/// Physically implausible readings (the emulator's -999 C dropout sentinel) are flagged by a range
/// gate and never enter a detector window. The IID detector's p-value is relative to its recent
/// history, so one -999 in the window widens the reference spread until a real 245 C overheat
/// looks ordinary. Replaying the emulator's fault model measured 0 of 704 overheats flagged
/// before the gate and 703 of 704 after it (see DetectorQualityTests).
/// </remarks>
public sealed class ModelEngine : IDisposable
{
    public const int HistoryLength = 20;

    // An engine-block thermocouple reads roughly -50..400 C. Anything outside is a sensor fault.
    public const double PlausibleMinCelsius = -50;
    public const double PlausibleMaxCelsius = 400;

    public const string IidSpikeMethod = "iid-spike";
    public const string RangeGateMethod = "range-gate";
    private const int MaxEquipmentStates = 10_000;

    private readonly MLContext _mlContext = new();
    private readonly ITransformer _model;
    private readonly ConcurrentDictionary<string, EngineState> _engines = new(
        StringComparer.Ordinal
    );

    public ModelEngine(string modelPath)
    {
        DetectorVersion = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(modelPath)))
            .ToLowerInvariant();
        _model = _mlContext.Model.Load(modelPath, out _);
    }

    /// <summary>SHA-256 of the exact detector artifact loaded by this process.</summary>
    public string DetectorVersion { get; }

    public static bool IsPlausible(double celsius) =>
        double.IsFinite(celsius) && celsius is >= PlausibleMinCelsius and <= PlausibleMaxCelsius;

    /// <summary>
    /// Scores one reading. <paramref name="loadHistory"/> runs only when this equipment has no
    /// in-memory state and should return plausible readings in the order they were scored.
    /// </summary>
    public async Task<MachineHealthResult> InspectAsync(
        string equipmentId,
        float temperature,
        Func<CancellationToken, Task<IReadOnlyList<float>>> loadHistory,
        CancellationToken cancellationToken
    )
    {
        if (!IsPlausible(temperature))
        {
            // Leaves any detector state untouched; the reading is a sensor fault, not a sample.
            var historyCount = _engines.TryGetValue(equipmentId, out var existing)
                ? Volatile.Read(ref existing.ObservationCount)
                : 0;
            return new MachineHealthResult(
                true,
                temperature,
                0,
                historyCount,
                DetectorVersion,
                RangeGateMethod
            );
        }

        if (!_engines.TryGetValue(equipmentId, out var state))
        {
            var history = await loadHistory(cancellationToken);
            var candidate = CreateState(history);
            state = _engines.GetOrAdd(equipmentId, candidate);

            if (ReferenceEquals(state, candidate))
                TrimCacheIfNeeded(equipmentId);
        }

        lock (state.Gate)
        {
            state.LastUsed = Environment.TickCount64;
            var historyCount = state.ObservationCount;
            var prediction = state.Engine.Predict(
                new TelemetryData { EngineTemperature = temperature }
            );
            if (state.ObservationCount < HistoryLength)
                state.ObservationCount++;

            return new MachineHealthResult(
                prediction.Prediction[0] == 1,
                prediction.Prediction[1],
                prediction.Prediction[2],
                historyCount,
                DetectorVersion,
                IidSpikeMethod
            );
        }
    }

    // After a failed database transaction the in-memory window is one reading ahead of durable
    // state, so the retry rebuilds this equipment's engine from Postgres before scoring.
    public void Invalidate(string equipmentId) => _engines.TryRemove(equipmentId, out _);

    private EngineState CreateState(IReadOnlyList<float> history)
    {
        var engine = _model.CreateTimeSeriesEngine<TelemetryData, AnomalyPrediction>(_mlContext);

        // The caller already filters in SQL; this guards any other history source.
        var plausible = history.Where(temperature => IsPlausible(temperature)).ToArray();
        foreach (var temperature in plausible)
            engine.Predict(new TelemetryData { EngineTemperature = temperature });

        return new EngineState(engine, Math.Min(plausible.Length, HistoryLength));
    }

    private void TrimCacheIfNeeded(string currentEquipmentId)
    {
        if (_engines.Count <= MaxEquipmentStates)
            return;

        var oldest = _engines
            .Where(pair => pair.Key != currentEquipmentId)
            .MinBy(pair => Volatile.Read(ref pair.Value.LastUsed));

        if (!string.IsNullOrEmpty(oldest.Key))
            _engines.TryRemove(oldest.Key, out _);
    }

    public void Dispose() => _engines.Clear();

    private sealed class EngineState(
        TimeSeriesPredictionEngine<TelemetryData, AnomalyPrediction> engine,
        int observationCount
    )
    {
        public object Gate { get; } = new();
        public TimeSeriesPredictionEngine<TelemetryData, AnomalyPrediction> Engine { get; } = engine;
        public int ObservationCount = observationCount;
        public long LastUsed = Environment.TickCount64;
    }
}
