using Confluent.Kafka;

using Industrial.Diagnostics.Worker.Configuration;
using Industrial.Diagnostics.Worker.Infrastructure.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Industrial.Diagnostics.Worker.Infrastructure;

/// <summary>Tracks whether the source consumer currently owns at least one partition.</summary>
public sealed class DiagnosticsConsumerReadiness : IHealthCheck
{
    private int _assignedPartitions;

    public void PartitionsAssigned(int count) =>
        Interlocked.Exchange(ref _assignedPartitions, Math.Max(0, count));

    public void PartitionsRevoked() => Interlocked.Exchange(ref _assignedPartitions, 0);

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    )
    {
        var count = Volatile.Read(ref _assignedPartitions);
        return Task.FromResult(
            count > 0
                ? HealthCheckResult.Healthy($"Kafka consumer owns {count} partitions.")
                : HealthCheckResult.Unhealthy("Kafka consumer has no partition assignment.")
        );
    }
}

/// <summary>Checks all Kafka topics the diagnostics process must read or write.</summary>
public sealed class DiagnosticsKafkaReadinessCheck(
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
            var expected = new HashSet<string>(
                [kafka.EventsTopic, kafka.AlertsTopic, kafka.DeadLetterTopic],
                StringComparer.Ordinal
            );
            var metadata = adminClient.GetMetadata(
                TimeSpan.FromSeconds(kafka.ReadinessTimeoutSeconds)
            );

            foreach (var topicName in expected)
            {
                var topic = metadata.Topics.SingleOrDefault(candidate =>
                    string.Equals(candidate.Topic, topicName, StringComparison.Ordinal)
                );
                if (topic is null || topic.Error.IsError || topic.Partitions.Count == 0)
                {
                    return Task.FromResult(
                        HealthCheckResult.Unhealthy(
                            $"Kafka topic '{topicName}' has no usable metadata."
                        )
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
                    return Task.FromResult(
                        HealthCheckResult.Unhealthy(
                            $"Kafka topic '{topicName}' has an unavailable partition."
                        )
                    );
                }
            }

            return Task.FromResult(HealthCheckResult.Healthy("Kafka topics are available."));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Task.FromResult(
                HealthCheckResult.Unhealthy("Kafka metadata request failed.", exception)
            );
        }
    }
}

/// <summary>Requires a reachable Postgres database with the current EF migration set applied.</summary>
public sealed class DiagnosticsDatabaseReadinessCheck(
    IDbContextFactory<AppDbContext> contextFactory
) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            if (!await db.Database.CanConnectAsync(cancellationToken))
                return HealthCheckResult.Unhealthy("Postgres is unreachable.");

            var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
            return pending.Count == 0
                ? HealthCheckResult.Healthy("Postgres is reachable and migrations are current.")
                : HealthCheckResult.Unhealthy(
                    $"Postgres has {pending.Count} pending migration(s)."
                );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("Postgres readiness check failed.", exception);
        }
    }
}
