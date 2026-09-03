using Confluent.Kafka;
using Industrial.Web.Api.Infrastructure;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Industrial.Web.Tests;

public sealed class KafkaTopicsHealthCheckTests
{
    private static readonly string[] Topics = ["telemetry-events", "telemetry-alerts"];

    [Fact]
    public void ReportsHealthyWhenEveryTopicHasAUsablePartition()
    {
        var result = KafkaTopicsHealthCheck.EvaluateMetadata(
            Topics,
            MetadataFor(Topics.Select(HealthyTopic).ToArray())
        );

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public void ReportsUnhealthyWhenARequiredTopicIsMissing()
    {
        var result = KafkaTopicsHealthCheck.EvaluateMetadata(
            Topics,
            MetadataFor([HealthyTopic(Topics[0])])
        );

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains(Topics[1], result.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsUnhealthyWhenAPartitionHasNoLeader()
    {
        var unavailable = new TopicMetadata(
            Topics[1],
            [new PartitionMetadata(0, -1, [1], [1], new Error(ErrorCode.NoError))],
            new Error(ErrorCode.NoError)
        );

        var result = KafkaTopicsHealthCheck.EvaluateMetadata(
            Topics,
            MetadataFor([HealthyTopic(Topics[0]), unavailable])
        );

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    private static TopicMetadata HealthyTopic(string topic) =>
        new(
            topic,
            [new PartitionMetadata(0, 1, [1], [1], new Error(ErrorCode.NoError))],
            new Error(ErrorCode.NoError)
        );

    private static Metadata MetadataFor(IReadOnlyList<TopicMetadata> topics) =>
        new([new BrokerMetadata(1, "broker", 9092)], topics.ToList(), 1, "broker");
}
