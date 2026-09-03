using System.Runtime.CompilerServices;

using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Transforms.TimeSeries;

// Helper to get the actual folder where this file lives
string GetSourceDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

var localDir = GetSourceDir();
var modelPath = Path.GetFullPath(
    Path.Combine(localDir, "..", "Industrial.Diagnostics.Worker", "model.zip")
);

Console.WriteLine("Building online IID spike-detector configuration...");

var mlContext = new MLContext();

// DetectIidSpike is an online statistical detector. Fit validates the schema but does not learn
// parameters from historical rows, so one representative value is sufficient to build the
// transformer. The worker warms each equipment's independent state from its recent Postgres rows.
var schemaData = mlContext.Data.LoadFromEnumerable(
    new[] { new TelemetryData { EngineTemperature = 90.0f } }
);

var pipeline = mlContext.Transforms.DetectIidSpike(
    outputColumnName: nameof(AnomalyPrediction.Prediction),
    inputColumnName: nameof(TelemetryData.EngineTemperature),
    confidence: 90.0,
    pvalueHistoryLength: 20
);

var model = pipeline.Fit(schemaData);

mlContext.Model.Save(model, schemaData.Schema, modelPath);
Console.WriteLine($"✅ Model saved to: {modelPath}");

Console.WriteLine("\n--- Testing Model Locally ---");
var predictionEngine = model.CreateTimeSeriesEngine<TelemetryData, AnomalyPrediction>(mlContext);

// Warm up the engine with 50 "Normal" points
for (int i = 0; i < 50; i++)
{
    float normalWithNoise = 85.0f + (float)(Random.Shared.NextDouble() * 15);
    predictionEngine.Predict(new TelemetryData { EngineTemperature = normalWithNoise });
}

// Test Normal (Inside the 85-100 range)
var normalResult = predictionEngine.Predict(new TelemetryData { EngineTemperature = 92.0f });
Console.WriteLine($"Normal temp (92.0) -> Is Anomaly? {normalResult.Prediction[0] == 1}");

// Test Spike (Way outside the 85-100 range)
var spikeResult = predictionEngine.Predict(new TelemetryData { EngineTemperature = 250.0f });
Console.WriteLine($"Spike temp (250.0) -> Is Anomaly? {spikeResult.Prediction[0] == 1}");

public class TelemetryData
{
    [LoadColumn(2)]
    public float EngineTemperature { get; set; }
}

public class AnomalyPrediction
{
    [VectorType(3)]
    public double[] Prediction { get; set; } = default!;
}
