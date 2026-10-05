using Industrial.Diagnostics.Worker.Features.Diagnostics.Advice;

namespace Industrial.Diagnostics.Tests;

public sealed class DiagnosisAdvisorTests
{
    // A healthy history like the emulator's: uniform-ish 70..110 C, with one earlier 245 C
    // overheat and one -999 dropout that a robust baseline must shrug off.
    private static readonly double[] History =
    [
        88, 92, 75, 101, 84, 245, 96, 79, 105, 90, -999, 83, 99, 77, 94, 86, 108, 81, 97, 89,
    ];

    private static DiagnosisEvidence Evidence(
        double temperature,
        IReadOnlyList<double>? history = null,
        double? oilPressure = 45
    ) =>
        new(
            "EQ-3",
            DateTimeOffset.Parse("2026-10-05T12:00:00Z"),
            temperature,
            oilPressure,
            DetectorScore: 245,
            DetectorPValue: 0.0012,
            DetectorHistoryCount: 20,
            history ?? History
        );

    [Fact]
    public void Dropout_sentinel_is_a_sensor_fault_not_a_thermal_event()
    {
        var analysis = RuleBasedDiagnosisAdvisor.Analyze(Evidence(-999));
        var text = RuleBasedDiagnosisAdvisor.Diagnose(Evidence(-999));

        Assert.Equal(DiagnosisPattern.SensorFault, analysis.Pattern);
        Assert.Contains("outside the sensor's physical range", text);
        Assert.Contains("sensor or wiring fault", text);
    }

    [Fact]
    public void Overheat_uses_a_robust_baseline_that_ignores_earlier_faults()
    {
        var analysis = RuleBasedDiagnosisAdvisor.Analyze(Evidence(245));

        Assert.Equal(DiagnosisPattern.SuddenSpike, analysis.Pattern);
        // The -999 is excluded as implausible and the earlier 245 barely moves the median.
        Assert.Equal(19, analysis.BaselineCount);
        Assert.InRange(analysis.BaselineMedian!.Value, 88, 92);
        Assert.True(analysis.RobustZ > 8, $"robust z was {analysis.RobustZ}");
    }

    [Fact]
    public void Overheat_with_low_oil_pressure_points_at_lubrication()
    {
        var text = RuleBasedDiagnosisAdvisor.Diagnose(Evidence(245, oilPressure: 31));

        Assert.Contains("lubrication loss", text);
        Assert.Contains("Oil pressure 31.0", text);
    }

    [Fact]
    public void Five_consecutive_rises_are_a_trend_rather_than_a_spike()
    {
        double[] history = [85, 90, 80, 88, 84, 86, 92, 98, 104, 109];

        var analysis = RuleBasedDiagnosisAdvisor.Analyze(Evidence(140, history));

        Assert.Equal(DiagnosisPattern.RisingTrend, analysis.Pattern);
    }

    [Fact]
    public void Sharp_drop_is_classified_below_baseline()
    {
        Assert.Equal(
            DiagnosisPattern.SuddenDrop,
            RuleBasedDiagnosisAdvisor.Analyze(Evidence(5)).Pattern
        );
    }

    [Fact]
    public void Short_history_says_so_instead_of_guessing()
    {
        var analysis = RuleBasedDiagnosisAdvisor.Analyze(Evidence(245, [90, -999, 95]));

        Assert.Equal(DiagnosisPattern.InsufficientHistory, analysis.Pattern);
        Assert.Equal(2, analysis.BaselineCount);
    }

    [Fact]
    public async Task Offline_advice_is_deterministic_and_has_three_steps()
    {
        var advisor = new RuleBasedDiagnosisAdvisor();

        var first = await advisor.DiagnoseAsync(Evidence(245), CancellationToken.None);
        var second = await advisor.DiagnoseAsync(Evidence(245), CancellationToken.None);

        Assert.Equal(first, second);
        Assert.StartsWith("[offline rule-based diagnosis] EQ-3 read 245.0 C", first);
        Assert.Contains(" 1. ", first);
        Assert.Contains(" 2. ", first);
        Assert.Contains(" 3. ", first);
        Assert.DoesNotContain(" 4. ", first);
        Assert.Contains("p-value 0.0012", first);
    }

    [Fact]
    public void Gemini_prompt_carries_the_computed_evidence()
    {
        var prompt = GeminiDiagnosisAdvisor.BuildPrompt(Evidence(245, oilPressure: 31));

        Assert.Contains("Rule-based pattern: SuddenSpike", prompt);
        Assert.Contains("robust z", prompt);
        Assert.Contains("p-value 0.0012", prompt);
        Assert.Contains("Oil pressure 31.0", prompt);
        Assert.Contains("Previous temperatures (C, oldest first): -999.0, 83.0, 99.0", prompt);
        Assert.Contains("exactly 3 numbered corrective steps", prompt);
    }
}
