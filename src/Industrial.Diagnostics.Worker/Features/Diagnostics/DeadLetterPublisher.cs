using System.Globalization;
using System.Text;
using System.Text.Json;

using Confluent.Kafka;

using Industrial.Diagnostics.Worker.Configuration;
using Industrial.Diagnostics.Worker.Infrastructure;

using Microsoft.Extensions.Options;

namespace Industrial.Diagnostics.Worker.Features.Diagnostics;

/// <summary>
/// The small transport seam keeps DLQ construction and acknowledgement behavior testable without
/// requiring a Kafka broker.
/// </summary>
public interface IDeadLetterTransport
{
    Task<PersistenceStatus> PublishAsync(
        string topic,
        Message<string, string> message,
        CancellationToken cancellationToken
    );
}

public sealed class KafkaDeadLetterTransport(IProducer<string, string> producer)
    : IDeadLetterTransport
{
    public async Task<PersistenceStatus> PublishAsync(
        string topic,
        Message<string, string> message,
        CancellationToken cancellationToken
    )
    {
        var delivery = await producer.ProduceAsync(topic, message, cancellationToken);
        return delivery.Status;
    }
}

public interface IDeadLetterPublisher
{
    Task PublishAsync(
        ConsumeResult<string, string> source,
        string failureReason,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Publishes unusable source records to a separate Kafka topic before their source offsets are
/// committed. A failed or ambiguous acknowledgement is an exception, so the consumer rewinds and
/// retries the source record. This makes quarantine at-least-once; consumers should deduplicate on
/// the stable SourceIdentity when an ACK was persisted but not observed.
/// </summary>
public sealed class DeadLetterPublisher(
    IDeadLetterTransport transport,
    IOptions<KafkaOptions> kafkaOptions,
    WorkerMetrics metrics
) : IDeadLetterPublisher
{
    internal const int MaxPayloadUtf8Bytes = 64 * 1024;
    internal const int MaxReasonUtf8Bytes = 1_024;
    internal const int MaxMetadataUtf8Bytes = 1_024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task PublishAsync(
        ConsumeResult<string, string> source,
        string failureReason,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);

        var envelope = CreateEnvelope(source, failureReason, DateTimeOffset.UtcNow);
        var traceParent = envelope.TraceParent;
        var message = new Message<string, string>
        {
            // The identity is stable across source retries. Kafka may contain duplicate DLQ
            // records after an ambiguous ACK, but downstream tooling can collapse them by key.
            Key = envelope.SourceIdentity,
            Value = JsonSerializer.Serialize(envelope, JsonOptions),
            Headers =
            [
                new Header("dlq-source-topic", Encoding.UTF8.GetBytes(envelope.SourceTopic)),
                new Header(
                    "dlq-source-partition",
                    Encoding.UTF8.GetBytes(
                        envelope.SourcePartition.ToString(CultureInfo.InvariantCulture)
                    )
                ),
                new Header(
                    "dlq-source-offset",
                    Encoding.UTF8.GetBytes(
                        envelope.SourceOffset.ToString(CultureInfo.InvariantCulture)
                    )
                ),
            ],
        };

        if (!string.IsNullOrEmpty(traceParent))
            message.Headers.Add("traceparent", Encoding.UTF8.GetBytes(traceParent));

        try
        {
            var status = await transport.PublishAsync(
                kafkaOptions.Value.DeadLetterTopic,
                message,
                cancellationToken
            );

            if (status != PersistenceStatus.Persisted)
            {
                throw new KafkaException(
                    new Error(
                        ErrorCode.Local_MsgTimedOut,
                        $"Kafka reported {status} for dead-letter record {envelope.SourceIdentity}."
                    )
                );
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            metrics.DeadLetterFailures.Add(1);
            throw;
        }

        metrics.DeadLettersPublished.Add(1);
    }

    internal static DeadLetterEnvelope CreateEnvelope(
        ConsumeResult<string, string> source,
        string failureReason,
        DateTimeOffset quarantinedAt
    )
    {
        var payload = TruncateUtf8(
            source.Message?.Value,
            MaxPayloadUtf8Bytes,
            out var payloadTruncated
        );
        var sourceKey = TruncateUtf8(
            source.Message?.Key,
            MaxMetadataUtf8Bytes,
            out var sourceKeyTruncated
        );
        var traceParent = ReadBoundedHeader(source.Message?.Headers, "traceparent");
        var reason = TruncateUtf8(failureReason, MaxReasonUtf8Bytes, out var reasonTruncated)!;
        DateTimeOffset? sourceTimestamp =
            source.Message is not null
            && source.Message.Timestamp.Type != TimestampType.NotAvailable
                ? new DateTimeOffset(source.Message.Timestamp.UtcDateTime)
                : null;

        return new DeadLetterEnvelope(
            SchemaVersion: 1,
            SourceIdentity: $"{source.Topic}:{source.Partition.Value}:{source.Offset.Value}",
            SourceTopic: source.Topic,
            SourcePartition: source.Partition.Value,
            SourceOffset: source.Offset.Value,
            SourceTimestamp: sourceTimestamp,
            SourceKey: sourceKey,
            SourceKeyTruncated: sourceKeyTruncated,
            TraceParent: traceParent,
            FailureReason: reason,
            FailureReasonTruncated: reasonTruncated,
            Payload: payload,
            PayloadTruncated: payloadTruncated,
            QuarantinedAt: quarantinedAt
        );
    }

    private static string? ReadBoundedHeader(Headers? headers, string name)
    {
        if (headers is null || !headers.TryGetLastBytes(name, out var bytes) || bytes is null)
            return null;

        var value = Encoding.UTF8.GetString(bytes);
        return TruncateUtf8(value, MaxMetadataUtf8Bytes, out _);
    }

    internal static string? TruncateUtf8(string? value, int maxBytes, out bool truncated)
    {
        if (value is null)
        {
            truncated = false;
            return null;
        }

        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
        {
            truncated = false;
            return value;
        }

        var builder = new StringBuilder(Math.Min(value.Length, maxBytes));
        var byteCount = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (byteCount + rune.Utf8SequenceLength > maxBytes)
                break;

            builder.Append(rune);
            byteCount += rune.Utf8SequenceLength;
        }

        truncated = true;
        return builder.ToString();
    }
}

internal sealed record DeadLetterEnvelope(
    int SchemaVersion,
    string SourceIdentity,
    string SourceTopic,
    int SourcePartition,
    long SourceOffset,
    DateTimeOffset? SourceTimestamp,
    string? SourceKey,
    bool SourceKeyTruncated,
    string? TraceParent,
    string FailureReason,
    bool FailureReasonTruncated,
    string? Payload,
    bool PayloadTruncated,
    DateTimeOffset QuarantinedAt
);
