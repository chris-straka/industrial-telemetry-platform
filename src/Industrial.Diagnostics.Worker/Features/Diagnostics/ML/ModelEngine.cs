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

public record MachineHealthResult(
    bool IsAnomaly,
    double Score,
    double PValue,
    int HistoryCount,
    string DetectorVersion
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
/// </remarks>
public sealed class ModelEngine : IDisposable
{
    public const int HistoryLength = 20;
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

    public async Task<MachineHealthResult> InspectAsync(
        string equipmentId,
        float temperature,
        Func<CancellationToken, Task<IReadOnlyList<float>>> loadHistory,
        CancellationToken cancellationToken
    )
    {
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
                DetectorVersion
            );
        }
    }

    // A failed database transaction must not leave the in-memory window one reading ahead of
    // durable state. The retry recreates this equipment's engine from Postgres before scoring.
    public void Invalidate(string equipmentId) => _engines.TryRemove(equipmentId, out _);

    private EngineState CreateState(IReadOnlyList<float> history)
    {
        var engine = _model.CreateTimeSeriesEngine<TelemetryData, AnomalyPrediction>(_mlContext);

        foreach (var temperature in history)
            engine.Predict(new TelemetryData { EngineTemperature = temperature });

        return new EngineState(engine, Math.Min(history.Count, HistoryLength));
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
