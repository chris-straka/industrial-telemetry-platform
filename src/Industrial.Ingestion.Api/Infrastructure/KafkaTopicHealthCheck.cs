using Confluent.Kafka;

using Industrial.Ingestion.Api.Configuration;

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Industrial.Ingestion.Api.Infrastructure;

/// <summary>
/// Proves the configured events topic has usable leaders without producing a probe record.
/// </summary>
public sealed class KafkaTopicHealthCheck(
    IAdminClient adminClient,
    IOptions<KafkaOptions> kafkaOptions
) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    )
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<HealthCheckResult>(cancellationToken);

        var kafka = kafkaOptions.Value;

        try
        {
            // GetMetadata is synchronous, but the native client enforces this timeout. The
            // readiness request therefore cannot wait indefinitely when Kafka is unavailable.
            var metadata = adminClient.GetMetadata(
                kafka.EventsTopic,
                TimeSpan.FromSeconds(kafka.ReadinessTimeoutSeconds)
            );
            return Task.FromResult(EvaluateMetadata(kafka.EventsTopic, metadata));
        }
        catch (KafkaException ex)
        {
            return Task.FromResult(
                HealthCheckResult.Unhealthy("Kafka metadata request failed.", ex)
            );
        }
        catch (TimeoutException ex)
        {
            return Task.FromResult(
                HealthCheckResult.Unhealthy("Kafka metadata request timed out.", ex)
            );
        }
        catch (Exception ex)
        {
            return Task.FromResult(
                HealthCheckResult.Unhealthy("Kafka readiness check failed.", ex)
            );
        }
    }

    internal static HealthCheckResult EvaluateMetadata(string eventsTopic, Metadata metadata)
    {
        var topic = metadata.Topics.SingleOrDefault(candidate =>
            string.Equals(candidate.Topic, eventsTopic, StringComparison.Ordinal)
        );

        if (topic is null)
        {
            return HealthCheckResult.Unhealthy(
                $"Kafka returned no metadata for topic '{eventsTopic}'."
            );
        }

        if (topic.Error.IsError)
        {
            return HealthCheckResult.Unhealthy(
                $"Kafka topic '{eventsTopic}' is unavailable: {topic.Error.Reason}"
            );
        }

        if (topic.Partitions.Count == 0)
            return HealthCheckResult.Unhealthy($"Kafka topic '{eventsTopic}' has no partitions.");

        var unavailablePartition = topic.Partitions.FirstOrDefault(partition =>
            partition.Error.IsError || partition.Leader < 0 || !partition.InSyncReplicas.Any()
        );
        if (unavailablePartition is not null)
        {
            var reason = unavailablePartition.Error.IsError
                ? unavailablePartition.Error.Reason
                : "no leader or in-sync replica";
            return HealthCheckResult.Unhealthy(
                $"Kafka topic '{eventsTopic}' partition "
                    + $"{unavailablePartition.PartitionId} is unavailable: {reason}."
            );
        }

        return HealthCheckResult.Healthy(
            $"Kafka topic '{eventsTopic}' has {topic.Partitions.Count} ready partitions."
        );
    }
}
