using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Industrial.Diagnostics.Worker.Configuration;
using Industrial.Diagnostics.Worker.Features.Diagnostics;
using Industrial.Diagnostics.Worker.Infrastructure;
using Microsoft.Extensions.Options;

namespace Industrial.Diagnostics.Tests;

public sealed class DeadLetterPublisherTests
{
    [Fact]
    public async Task Persisted_delivery_preserves_source_identity_and_bounds_untrusted_fields()
    {
        var transport = new FakeDeadLetterTransport(PersistenceStatus.Persisted);
        using var metrics = new WorkerMetrics();
        var publisher = new DeadLetterPublisher(
            transport,
            Options.Create(ValidKafkaOptions()),
            metrics
        );
        var timestamp = DateTime.UtcNow.AddMinutes(-1);
        var headers = new Headers
        {
            new Header(
                "traceparent",
                Encoding.UTF8.GetBytes("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01")
            ),
        };
        var source = SourceRecord(
            key: new string('k', DeadLetterPublisher.MaxMetadataUtf8Bytes + 1),
            value: string.Concat(
                Enumerable.Repeat("🔥", DeadLetterPublisher.MaxPayloadUtf8Bytes)
            ),
            timestamp,
            headers
        );
        var reason = new string('r', DeadLetterPublisher.MaxReasonUtf8Bytes + 1);

        await publisher.PublishAsync(source, reason, CancellationToken.None);

        Assert.Equal("telemetry-events-dlq", transport.Topic);
        Assert.NotNull(transport.Message);
        Assert.Equal("telemetry-events:3:42", transport.Message.Key);
        Assert.Equal(
            "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
            Encoding.UTF8.GetString(transport.Message.Headers.GetLastBytes("traceparent"))
        );

        var envelope = JsonSerializer.Deserialize<DeadLetterEnvelope>(
            transport.Message.Value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
        );
        Assert.NotNull(envelope);
        Assert.Equal(1, envelope.SchemaVersion);
        Assert.Equal("telemetry-events:3:42", envelope.SourceIdentity);
        Assert.InRange(
            (timestamp - envelope.SourceTimestamp!.Value.UtcDateTime).Duration(),
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(1)
        );
        Assert.True(envelope.SourceKeyTruncated);
        Assert.True(envelope.FailureReasonTruncated);
        Assert.True(envelope.PayloadTruncated);
        Assert.True(
            Encoding.UTF8.GetByteCount(envelope.SourceKey!)
                <= DeadLetterPublisher.MaxMetadataUtf8Bytes
        );
        Assert.True(
            Encoding.UTF8.GetByteCount(envelope.FailureReason)
                <= DeadLetterPublisher.MaxReasonUtf8Bytes
        );
        Assert.True(
            Encoding.UTF8.GetByteCount(envelope.Payload!)
                <= DeadLetterPublisher.MaxPayloadUtf8Bytes
        );
    }

    [Theory]
    [InlineData(PersistenceStatus.NotPersisted)]
    [InlineData(PersistenceStatus.PossiblyPersisted)]
    public async Task Any_non_persisted_delivery_result_is_a_retryable_failure(
        PersistenceStatus status
    )
    {
        var transport = new FakeDeadLetterTransport(status);
        using var metrics = new WorkerMetrics();
        var publisher = new DeadLetterPublisher(
            transport,
            Options.Create(ValidKafkaOptions()),
            metrics
        );

        var exception = await Assert.ThrowsAsync<KafkaException>(() =>
            publisher.PublishAsync(
                SourceRecord(key: null, value: null, DateTime.UtcNow, new Headers()),
                "Kafka record is a tombstone",
                CancellationToken.None
            )
        );

        Assert.Contains(status.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public void Source_and_dead_letter_topics_must_be_distinct()
    {
        var options = ValidKafkaOptions();
        options.DeadLetterTopic = options.EventsTopic;
        var results = new List<ValidationResult>();

        var valid = Validator.TryValidateObject(
            options,
            new ValidationContext(options),
            results,
            validateAllProperties: true
        );

        Assert.False(valid);
        Assert.Contains(results, result => result.ErrorMessage!.Contains("must be distinct"));
    }

    private static ConsumeResult<string, string> SourceRecord(
        string? key,
        string? value,
        DateTime timestamp,
        Headers headers
    ) =>
        new()
        {
            Topic = "telemetry-events",
            Partition = new Partition(3),
            Offset = new Offset(42),
            Message = new Message<string, string>
            {
                Key = key!,
                Value = value!,
                Timestamp = new Timestamp(timestamp, TimestampType.CreateTime),
                Headers = headers,
            },
        };

    private static KafkaOptions ValidKafkaOptions() =>
        new()
        {
            BootstrapServers = "localhost:9094",
            GroupId = "diagnostics-tests",
            EventsTopic = "telemetry-events",
            AlertsTopic = "telemetry-alerts",
            DeadLetterTopic = "telemetry-events-dlq",
            ReadinessTimeoutSeconds = 3,
        };

    private sealed class FakeDeadLetterTransport(PersistenceStatus status)
        : IDeadLetterTransport
    {
        public int CallCount { get; private set; }
        public string? Topic { get; private set; }
        public Message<string, string>? Message { get; private set; }

        public Task<PersistenceStatus> PublishAsync(
            string topic,
            Message<string, string> message,
            CancellationToken cancellationToken
        )
        {
            CallCount++;
            Topic = topic;
            Message = message;
            return Task.FromResult(status);
        }
    }
}
