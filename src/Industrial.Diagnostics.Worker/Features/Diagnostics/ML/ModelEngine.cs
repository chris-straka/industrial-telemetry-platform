using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Transforms.TimeSeries;

namespace Industrial.Diagnostics.Worker.Features.Diagnostics.ML;

public class TelemetryData
{
    // Trainer sees this as Column 2
    [LoadColumn(2)]
    public float EngineTemperature { get; set; }
}

public class AnomalyPrediction
{
    // ChangePoint detection ALWAYS returns a vector of 3 doubles:
    // [0] Alert (0 or 1), [1] Score, [2] P-Value
    [VectorType(3)]
    public double[] Prediction { get; set; } = default!;
}

public record MachineHealthResult(bool IsAnomaly, double Score, double PValue);

public class ModelEngine
{
    private readonly TimeSeriesPredictionEngine<TelemetryData, AnomalyPrediction> _predictionEngine;

    public ModelEngine(string modelPath)
    {
        var mlContext = new MLContext();
        ITransformer model = mlContext.Model.Load(modelPath, out _);
        _predictionEngine = model.CreateTimeSeriesEngine<TelemetryData, AnomalyPrediction>(
            mlContext
        );
    }

    // Scores one reading against the detector's running window.
    // It's stateful in that each call advances the window as well
    public MachineHealthResult Inspect(float temp)
    {
        var p = _predictionEngine.Predict(new TelemetryData { EngineTemperature = temp });

        // Encapsulate the logic: Alert is index [0]
        bool isAnomaly = p.Prediction[0] == 1;

        return new MachineHealthResult(isAnomaly, p.Prediction[1], p.Prediction[2]);
    }
}
