using Confluent.Kafka;
using Industrial.Web.Api.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Industrial.Web.Api.Infrastructure;

/// <summary>
/// Checks broker connectivity and both topics independently of message traffic. This lets an idle
/// dashboard recover readiness as soon as Kafka recovers, without waiting for a new reading.
/// </summary>
public sealed class KafkaTopicsHealthCheck(
    IAdminClient adminClient,
    IOptions<KafkaOptions> options
) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    )
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<HealthCheckResult>(cancellationToken);

        try
        {
            var kafka = options.Value;
            var metadata = adminClient.GetMetadata(
                TimeSpan.FromSeconds(kafka.ReadinessTimeoutSeconds)
            );
            return Task.FromResult(
                EvaluateMetadata([kafka.EventsTopic, kafka.AlertsTopic], metadata)
            );
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Task.FromResult(
                HealthCheckResult.Unhealthy("Kafka metadata request failed.", exception)
            );
        }
    }

    public static HealthCheckResult EvaluateMetadata(
        IReadOnlyCollection<string> expectedTopics,
        Metadata metadata
    )
    {
        foreach (var topicName in expectedTopics.Distinct(StringComparer.Ordinal))
        {
            var topic = metadata.Topics.SingleOrDefault(candidate =>
                string.Equals(candidate.Topic, topicName, StringComparison.Ordinal)
            );
            if (topic is null || topic.Error.IsError || topic.Partitions.Count == 0)
            {
                return HealthCheckResult.Unhealthy(
                    $"Kafka topic '{topicName}' has no usable metadata."
                );
            }

            if (
                topic.Partitions.Any(partition =>
                    partition.Error.IsError
                    || partition.Leader < 0
                    || !partition.InSyncReplicas.Any()
                )
            )
            {
                return HealthCheckResult.Unhealthy(
                    $"Kafka topic '{topicName}' has an unavailable partition."
                );
            }
        }

        return HealthCheckResult.Healthy("Kafka dashboard topics are available.");
    }
}
