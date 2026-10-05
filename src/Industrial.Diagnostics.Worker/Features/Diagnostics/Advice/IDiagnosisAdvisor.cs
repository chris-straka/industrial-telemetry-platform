namespace Industrial.Diagnostics.Worker.Features.Diagnostics.Advice;

/// <summary>
/// Turns the evidence behind one anomaly into operator-facing text: a finding, a likely cause,
/// and three corrective steps.
/// </summary>
/// <remarks>
/// Advice is enrichment, never part of the decision. The anomaly row and its outbox row are
/// already committed when an advisor runs, so a slow or failing advisor can delay an alert's
/// text but cannot change whether the alert exists.
/// </remarks>
public interface IDiagnosisAdvisor
{
    /// <summary>Short provider name for logs and the diagnosis header.</summary>
    string Name { get; }

    Task<string> DiagnoseAsync(DiagnosisEvidence evidence, CancellationToken cancellationToken);
}
