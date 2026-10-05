using System.Globalization;
using System.Text;

using Industrial.Diagnostics.Worker.Features.Diagnostics.ML;

namespace Industrial.Diagnostics.Worker.Features.Diagnostics.Advice;

/// <summary>
/// Offline, deterministic diagnosis built from the detector's evidence. It is the default advisor
/// (no API key, no network, same text for the same evidence) and the fallback when an LLM call
/// fails or times out.
/// </summary>
public sealed class RuleBasedDiagnosisAdvisor : IDiagnosisAdvisor
{
    // Below this many plausible earlier readings a median is not a baseline.
    internal const int MinimumBaseline = 5;

    // A nearly constant history has MAD ~ 0, which would turn a 0.1 C wobble into a huge z.
    internal const double SigmaFloorCelsius = 1.0;

    // Five strictly rising readings in a row is a 1-in-120 event for independent noise.
    internal const int TrendLength = 5;

    // The emulator's healthy oil pressure is uniform in 30..60; the bottom sixth is "low".
    internal const double LowOilPressure = 35;

    public string Name => "offline";

    public Task<string> DiagnoseAsync(
        DiagnosisEvidence evidence,
        CancellationToken cancellationToken
    ) => Task.FromResult(Diagnose(evidence));

    public static string Diagnose(DiagnosisEvidence evidence)
    {
        var analysis = Analyze(evidence);
        var (cause, steps) = Advice(analysis);

        var text = new StringBuilder();
        text.Append("[offline rule-based diagnosis] ");
        text.Append(Finding(evidence, analysis));
        text.Append(" Likely cause: ").Append(cause).Append('.');
        for (var i = 0; i < steps.Length; i++)
            text.Append(CultureInfo.InvariantCulture, $" {i + 1}. {steps[i]}.");
        return text.ToString();
    }

    public static DiagnosisAnalysis Analyze(DiagnosisEvidence evidence)
    {
        var lowOil = evidence.OilPressure is < LowOilPressure;

        // Same bounds as the detector's range gate; the emulator's dropout fault reports -999 C.
        if (!ModelEngine.IsPlausible(evidence.EngineTemperature))
            return new DiagnosisAnalysis(DiagnosisPattern.SensorFault, 0, null, null, lowOil);

        var baseline = evidence.RecentTemperatures.Where(ModelEngine.IsPlausible).ToArray();
        if (baseline.Length < MinimumBaseline)
        {
            return new DiagnosisAnalysis(
                DiagnosisPattern.InsufficientHistory,
                baseline.Length,
                null,
                null,
                lowOil
            );
        }

        var median = Median(baseline);
        var mad = Median(baseline.Select(value => Math.Abs(value - median)).ToArray());
        var sigma = Math.Max(1.4826 * mad, SigmaFloorCelsius);
        var z = (evidence.EngineTemperature - median) / sigma;

        var pattern =
            z < 0 ? DiagnosisPattern.SuddenDrop
            : IsRisingTrend(baseline, evidence.EngineTemperature) ? DiagnosisPattern.RisingTrend
            : DiagnosisPattern.SuddenSpike;

        return new DiagnosisAnalysis(pattern, baseline.Length, median, z, lowOil);
    }

    internal static string Finding(DiagnosisEvidence evidence, DiagnosisAnalysis analysis)
    {
        var finding = new StringBuilder();
        finding.Append(
            CultureInfo.InvariantCulture,
            $"{evidence.EquipmentId} read {evidence.EngineTemperature:F1} C"
        );

        if (analysis is { BaselineMedian: { } median, RobustZ: { } z })
        {
            var delta = evidence.EngineTemperature - median;
            finding.Append(
                CultureInfo.InvariantCulture,
                $", {delta:+0.0;-0.0} C from its recent median of {median:F1} C (robust z {z:F1}, {analysis.BaselineCount} readings)"
            );
        }
        else if (analysis.Pattern == DiagnosisPattern.SensorFault)
        {
            finding.Append(", outside the sensor's physical range");
        }
        else
        {
            finding.Append(
                CultureInfo.InvariantCulture,
                $", with only {analysis.BaselineCount} plausible earlier readings to compare"
            );
        }

        finding.Append('.');
        if (evidence.DetectorPValue is { } pValue)
            finding.Append(CultureInfo.InvariantCulture, $" Detector p-value {pValue:F4}.");
        if (evidence.OilPressure is { } oil)
            finding.Append(CultureInfo.InvariantCulture, $" Oil pressure {oil:F1}.");
        return finding.ToString();
    }

    private static (string Cause, string[] Steps) Advice(DiagnosisAnalysis analysis) =>
        analysis.Pattern switch
        {
            DiagnosisPattern.SensorFault => (
                "temperature sensor or wiring fault, not a thermal event",
                [
                    "Inspect the probe connector and cable for a break or loose terminal",
                    "Cross-check engine temperature with a handheld or redundant sensor",
                    "Replace the probe if the out-of-range reading repeats; exclude these readings from trend reports",
                ]
            ),
            DiagnosisPattern.InsufficientHistory => (
                "unknown; there is not enough recent history to judge",
                [
                    "Confirm the reading on site before acting",
                    "Keep the equipment under observation until a baseline builds up",
                    "Escalate if the next readings stay outside the expected operating range",
                ]
            ),
            DiagnosisPattern.SuddenSpike when analysis.LowOilPressure => (
                "lubrication loss: overheating together with low oil pressure",
                [
                    "Reduce load or stop the equipment to prevent bearing damage",
                    "Check oil level, oil pump, and filter for blockage or leaks",
                    "Inspect bearings for wear before returning to service",
                ]
            ),
            DiagnosisPattern.SuddenSpike => (
                "sudden overheating, typically a cooling failure (coolant, fan, or blocked airflow)",
                [
                    "Reduce load or stop the equipment until the temperature recovers",
                    "Check coolant level, coolant pump, and radiator fan operation",
                    "Clear blocked air intakes or radiator fins, then watch the next readings",
                ]
            ),
            DiagnosisPattern.RisingTrend => (
                "progressive overheating: a degrading cooling or lubrication path",
                [
                    "Schedule inspection before the trend reaches the alarm limit",
                    "Check for fouled heat exchangers, a slipping fan belt, or degraded oil",
                    "Lower the duty cycle and confirm the trend flattens",
                ]
            ),
            _ => (
                "sudden temperature drop: probe detached, machine stopped, or a coolant surge",
                [
                    "Confirm whether the equipment is still running",
                    "Check that the probe is seated against the engine block",
                    "Inspect the thermostat for being stuck open",
                ]
            ),
        };

    private static bool IsRisingTrend(double[] baseline, double current)
    {
        if (baseline.Length < TrendLength)
            return false;

        var tail = baseline[^TrendLength..];
        for (var i = 1; i < tail.Length; i++)
        {
            if (tail[i] <= tail[i - 1])
                return false;
        }
        return current > tail[^1];
    }

    private static double Median(double[] values)
    {
        var sorted = values.Order().ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2;
    }
}
