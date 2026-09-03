using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Industrial.Web.Api.Infrastructure;

/// <summary>
/// Tracks whether this process's broadcast consumer currently owns Kafka partitions.
/// </summary>
public sealed class KafkaConsumerReadiness : IHealthCheck
{
    private int _assignedPartitions;

    public void PartitionsAssigned(int count)
        => Interlocked.Exchange(ref _assignedPartitions, Math.Max(0, count));

    public void PartitionsRevoked()
        => Interlocked.Exchange(ref _assignedPartitions, 0);

    public void PartitionsLost()
        => Interlocked.Exchange(ref _assignedPartitions, 0);

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    )
    {
        var assignedPartitions = Volatile.Read(ref _assignedPartitions);
        var result = assignedPartitions > 0
            ? HealthCheckResult.Healthy(
                $"Kafka consumer owns {assignedPartitions} partitions."
            )
            : HealthCheckResult.Unhealthy("The Kafka consumer has no partition assignment.");

        return Task.FromResult(result);
    }
}
