namespace Industrial.Diagnostics.Worker.Features.Diagnostics.Advice;

/// <summary>
/// Everything a diagnosis may cite about one anomalous reading. It is read from Postgres after the
/// anomaly row committed, so an advisor explains the same evidence the detector decided on.
/// </summary>
/// <remarks>
/// <c>RecentTemperatures</c> holds the equipment's readings before this one, oldest first, at most
/// <see cref="RecentWindow"/> long. Earlier faults are included on purpose; the analysis uses
/// robust statistics so they do not drag the baseline.
/// </remarks>
public sealed record DiagnosisEvidence(
    string EquipmentId,
    DateTimeOffset OccurredAt,
    double EngineTemperature,
    double? OilPressure,
    double? DetectorScore,
    double? DetectorPValue,
    int? DetectorHistoryCount,
    IReadOnlyList<double> RecentTemperatures
)
{
    /// <summary>How many earlier readings the baseline looks at.</summary>
    public const int RecentWindow = 20;
}

/// <summary>The failure pattern a reading matches, in the order the rules test them.</summary>
public enum DiagnosisPattern
{
    /// <summary>Outside the probe's physical range; the sensor, not the machine, is suspect.</summary>
    SensorFault,

    /// <summary>Too few plausible earlier readings to say what normal looks like.</summary>
    InsufficientHistory,

    /// <summary>Above a flat baseline in one step.</summary>
    SuddenSpike,

    /// <summary>Above baseline after several consecutive rises.</summary>
    RisingTrend,

    /// <summary>Well below baseline.</summary>
    SuddenDrop,
}

/// <summary>
/// Deterministic summary of the evidence. Both advisors start from it, so the LLM prompt and the
/// offline text cite identical numbers.
/// </summary>
/// <remarks>
/// <c>RobustZ</c> is (reading - median) / (1.4826 * MAD). The 1.4826 factor makes MAD estimate a
/// standard deviation for normal data; unlike mean/stddev, one earlier 245 C spike barely moves it.
/// </remarks>
public sealed record DiagnosisAnalysis(
    DiagnosisPattern Pattern,
    int BaselineCount,
    double? BaselineMedian,
    double? RobustZ,
    bool LowOilPressure
);
