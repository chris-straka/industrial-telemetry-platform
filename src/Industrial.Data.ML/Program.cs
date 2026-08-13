using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Transforms.TimeSeries;

// Helper to get the actual folder where this file lives
string GetSourceDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

var localDir = GetSourceDir();
var csvPath = Path.Combine(localDir, "training_data.csv");
var modelPath = Path.GetFullPath(
    Path.Combine(localDir, "..", "Industrial.Diagnostics.Worker", "model.zip")
);

var records = 10000;
var startTime = DateTime.UtcNow.AddDays(-1);

Console.WriteLine($"Generating training data at: {csvPath}");

using (var writer = new StreamWriter(csvPath))
{
    writer.WriteLine("Timestamp,EquipmentId,EngineTemperature,OilPressure");

    for (int i = 0; i < records; i++)
    {
        var timestamp = startTime.AddSeconds(i * 10).ToString("o");
        var equipmentId = $"EQ-{Random.Shared.Next(1, 5)}";

        double baseTemp = 85.0;
        double temp = baseTemp + (Random.Shared.NextDouble() * 15); // Range: 85-100
        double pressure = 40 + (Random.Shared.NextDouble() * 10);

        writer.WriteLine(
            $"{timestamp},{equipmentId},{temp.ToString("F2", CultureInfo.InvariantCulture)},{pressure.ToString("F2", CultureInfo.InvariantCulture)}"
        );
    }
}

Console.WriteLine("Training model...");

var mlContext = new MLContext();

var data = mlContext.Data.LoadFromTextFile<TelemetryData>(
    csvPath,
    hasHeader: true,
    separatorChar: ','
);

var pipeline = mlContext.Transforms.DetectIidSpike(
    outputColumnName: nameof(AnomalyPrediction.Prediction),
    inputColumnName: nameof(TelemetryData.EngineTemperature),
    confidence: 90.0,
    pvalueHistoryLength: 20
);

var model = pipeline.Fit(data);

mlContext.Model.Save(model, data.Schema, modelPath);
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
