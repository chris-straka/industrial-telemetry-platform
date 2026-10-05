using System.Globalization;
using System.Text;

using Google.GenAI;

using Industrial.Diagnostics.Worker.Configuration;

using Microsoft.Extensions.Options;

namespace Industrial.Diagnostics.Worker.Features.Diagnostics.Advice;

/// <summary>
/// Opt-in LLM advisor (<c>Diagnosis:Provider=Gemini</c>). The prompt carries the same computed
/// evidence as the offline advisor, so the model explains numbers instead of guessing from a bare
/// temperature.
/// </summary>
public sealed class GeminiDiagnosisAdvisor(Client client, IOptions<GeminiOptions> options)
    : IDiagnosisAdvisor
{
    public string Name => "gemini";

    public async Task<string> DiagnoseAsync(
        DiagnosisEvidence evidence,
        CancellationToken cancellationToken
    )
    {
        var response = await client.Models.GenerateContentAsync(
            model: options.Value.Model,
            contents: BuildPrompt(evidence),
            cancellationToken: cancellationToken
        );

        var text = response.Text;
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Gemini returned no text.");
        return text.Trim();
    }

    internal static string BuildPrompt(DiagnosisEvidence evidence)
    {
        var analysis = RuleBasedDiagnosisAdvisor.Analyze(evidence);
        var prompt = new StringBuilder();
        prompt.AppendLine(
            "You are a reliability engineer for industrial rotating equipment. An online IID spike "
                + "detector flagged the reading below. Use only the evidence given."
        );
        prompt.AppendLine();
        prompt.AppendLine(
            CultureInfo.InvariantCulture,
            $"Finding: {RuleBasedDiagnosisAdvisor.Finding(evidence, analysis)}"
        );
        prompt.AppendLine(CultureInfo.InvariantCulture, $"Rule-based pattern: {analysis.Pattern}");
        if (evidence.DetectorScore is { } score)
        {
            prompt.AppendLine(
                CultureInfo.InvariantCulture,
                $"Detector raw score: {score:F2}; warm-up history: {evidence.DetectorHistoryCount ?? 0} readings"
            );
        }
        if (evidence.RecentTemperatures.Count > 0)
        {
            var recent = string.Join(
                ", ",
                evidence.RecentTemperatures.TakeLast(10).Select(t =>
                    t.ToString("F1", CultureInfo.InvariantCulture)
                )
            );
            prompt.AppendLine(CultureInfo.InvariantCulture, $"Previous temperatures (C, oldest first): {recent}");
        }
        prompt.AppendLine();
        prompt.Append(
            "Reply in plain text under 120 words: the most likely cause in one sentence, then "
                + "exactly 3 numbered corrective steps. Say so if the evidence points to a sensor fault."
        );
        return prompt.ToString();
    }
}
