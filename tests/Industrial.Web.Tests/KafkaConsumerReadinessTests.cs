using Industrial.Web.Api.Infrastructure;

using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Industrial.Web.Tests;

public sealed class KafkaConsumerReadinessTests
{
    [Fact]
    public async Task StartsUnhealthyUntilPartitionsAreAssigned()
    {
        var readiness = new KafkaConsumerReadiness();

        var beforeAssignment = await CheckAsync(readiness);
        readiness.PartitionsAssigned(4);
        var afterAssignment = await CheckAsync(readiness);

        Assert.Equal(HealthStatus.Unhealthy, beforeAssignment.Status);
        Assert.Equal(HealthStatus.Healthy, afterAssignment.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevokedOrLostPartitionsMakeConsumerUnhealthy(bool lost)
    {
        var readiness = new KafkaConsumerReadiness();
        readiness.PartitionsAssigned(2);

        if (lost)
            readiness.PartitionsLost();
        else
            readiness.PartitionsRevoked();

        var result = await CheckAsync(readiness);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    private static Task<HealthCheckResult> CheckAsync(KafkaConsumerReadiness readiness) =>
        readiness.CheckHealthAsync(new HealthCheckContext());
}
