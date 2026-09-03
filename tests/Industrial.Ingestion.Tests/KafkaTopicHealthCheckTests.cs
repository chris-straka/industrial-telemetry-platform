using Confluent.Kafka;

using Industrial.Ingestion.Api.Infrastructure;

using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Industrial.Ingestion.Tests;

public sealed class KafkaTopicHealthCheckTests
{
    private const string EventsTopic = "telemetry-events";

    [Fact]
    public void ReportsHealthyOnlyWhenTopicPartitionsHaveLeadersAndReplicas()
    {
        var metadata = MetadataFor(
            new TopicMetadata(
                EventsTopic,
                [new PartitionMetadata(0, 1, [1], [1], new Error(ErrorCode.NoError))],
                new Error(ErrorCode.NoError)
            )
        );

        var result = KafkaTopicHealthCheck.EvaluateMetadata(EventsTopic, metadata);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public void ReportsUnhealthyWhenTopicIsMissing()
    {
        var result = KafkaTopicHealthCheck.EvaluateMetadata(
            EventsTopic,
            new Metadata([], [], 1, "broker")
        );

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("no metadata", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReportsUnhealthyWhenPartitionHasNoLeader()
    {
        var metadata = MetadataFor(
            new TopicMetadata(
                EventsTopic,
                [new PartitionMetadata(0, -1, [1], [1], new Error(ErrorCode.NoError))],
                new Error(ErrorCode.NoError)
            )
        );

        var result = KafkaTopicHealthCheck.EvaluateMetadata(EventsTopic, metadata);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("partition 0", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReportsUnhealthyWhenKafkaReturnsATopicError()
    {
        var metadata = MetadataFor(
            new TopicMetadata(
                EventsTopic,
                [],
                new Error(ErrorCode.UnknownTopicOrPart)
            )
        );

        var result = KafkaTopicHealthCheck.EvaluateMetadata(EventsTopic, metadata);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("unavailable", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    private static Metadata MetadataFor(TopicMetadata topic) =>
        new([new BrokerMetadata(1, "broker", 9092)], [topic], 1, "broker");
}
