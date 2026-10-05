using System.Text;
using System.Text.Json;

using Confluent.Kafka;

using Industrial.Diagnostics.Worker.Configuration;
using Industrial.Diagnostics.Worker.Features.Diagnostics.Advice;
using Industrial.Diagnostics.Worker.Infrastructure;
using Industrial.Diagnostics.Worker.Infrastructure.Data;
using Industrial.Shared;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Industrial.Diagnostics.Worker.Features.Diagnostics;

/// <summary>
/// Publishes committed anomaly alerts from the Postgres outbox to Kafka.
/// </summary>
public sealed class AlertOutboxPublisherWorker(
    IDbContextFactory<AppDbContext> contextFactory,
    IProducer<string, string> producer,
    IOptions<KafkaOptions> kafkaOptions,
    IOptions<OutboxOptions> outboxOptions,
    WorkerMetrics metrics,
    ILogger<AlertOutboxPublisherWorker> logger,
    IDiagnosisAdvisor advisor
) : BackgroundService
{
    private readonly TimeSpan _idleDelay = TimeSpan.FromMilliseconds(
        outboxOptions.Value.IdleDelayMs
    );
    private readonly TimeSpan _baseBackoff = TimeSpan.FromSeconds(
        outboxOptions.Value.BaseBackoffSeconds
    );
    private readonly TimeSpan _maxBackoff = TimeSpan.FromSeconds(
        outboxOptions.Value.MaxBackoffSeconds
    );
    private readonly TimeSpan _publishedRetention = TimeSpan.FromHours(
        outboxOptions.Value.PublishedRetentionHours
    );
    private readonly TimeSpan _maintenanceInterval = TimeSpan.FromSeconds(
        outboxOptions.Value.MaintenanceIntervalSeconds
    );
    private DateTimeOffset _nextMaintenanceAt = DateTimeOffset.MinValue;
    private int _consecutiveFailures;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await MaintainOutboxAsync(stoppingToken);

                switch (await PublishOneAsync(stoppingToken))
                {
                    case PublishOutcome.Empty:
                        _consecutiveFailures = 0;
                        await SafeDelayAsync(_idleDelay, stoppingToken);
                        break;
                    case PublishOutcome.Published:
                    case PublishOutcome.Enriched:
                        _consecutiveFailures = 0;
                        break;
                    case PublishOutcome.Failed:
                        await BackoffAsync(stoppingToken);
                        break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Alert outbox publisher failed before completing a row.");
                await BackoffAsync(stoppingToken);
            }
        }
    }

    private async Task MaintainOutboxAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (now < _nextMaintenanceAt)
            return;

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var cutoff = now - _publishedRetention;

        // Only published rows are pruned. A bounded window of delivery history keeps recent rows
        // auditable without letting this hot table grow without limit.
        var deleted = await db
            .AlertOutboxMessages.Where(row =>
                row.PublishedAt != null && row.PublishedAt < cutoff
            )
            .ExecuteDeleteAsync(cancellationToken);
        var pending = await db.AlertOutboxMessages.LongCountAsync(
            row => row.PublishedAt == null,
            cancellationToken
        );

        metrics.SetPendingAlertOutbox(pending);
        _nextMaintenanceAt = now + _maintenanceInterval;

        if (deleted > 0)
            logger.LogInformation("Pruned {Count} published alert outbox rows.", deleted);
    }

    private async Task<PublishOutcome> PublishOneAsync(CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // SKIP LOCKED lets several replicas drain the shared outbox without publishing the same
        // row concurrently. The lock is held through Kafka's ACK. If the process crashes, the
        // transaction rolls back and the row becomes visible for retry.
        var candidates = await db
            .AlertOutboxMessages.FromSqlRaw(
                """
                SELECT * FROM "AlertOutboxMessages"
                WHERE "PublishedAt" IS NULL
                ORDER BY "CreatedAt"
                LIMIT 1
                FOR UPDATE SKIP LOCKED
                """
            )
            .AsTracking()
            .ToListAsync(cancellationToken);

        var pending = candidates.SingleOrDefault();
        if (pending is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return PublishOutcome.Empty;
        }

        if (TryReadPendingEnrichment(pending.Payload, out var pendingAlert))
        {
            pending.Payload = await BuildEnrichedPayloadAsync(db, pendingAlert!, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return PublishOutcome.Enriched;
        }

        var alert = new Message<string, string>
        {
            Key = pending.EquipmentId,
            Value = pending.Payload,
        };

        if (!string.IsNullOrEmpty(pending.TraceParent))
        {
            alert.Headers =
            [
                new Header("traceparent", Encoding.UTF8.GetBytes(pending.TraceParent)),
            ];
        }

        try
        {
            var delivery = await producer.ProduceAsync(
                kafkaOptions.Value.AlertsTopic,
                alert,
                cancellationToken
            );

            if (delivery.Status != PersistenceStatus.Persisted)
            {
                throw new KafkaException(
                    new Error(
                        ErrorCode.Local_MsgTimedOut,
                        $"Kafka reported {delivery.Status} for outbox message {pending.Id}."
                    )
                );
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            pending.AttemptCount++;
            pending.LastError = Truncate(ex.Message, 2_000);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            metrics.AlertFailures.Add(1);
            logger.LogError(
                ex,
                "Alert outbox message {OutboxId} for {EquipmentId} was not published; it remains pending.",
                pending.Id,
                pending.EquipmentId
            );
            return PublishOutcome.Failed;
        }

        pending.PublishedAt = DateTimeOffset.UtcNow;
        pending.AttemptCount++;
        pending.LastError = null;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        metrics.AlertsPublished.Add(1);
        return PublishOutcome.Published;
    }

    private async Task<string> BuildEnrichedPayloadAsync(
        AppDbContext db,
        TelemetryAlertEnvelope alert,
        CancellationToken cancellationToken
    )
    {
        var evidence = await LoadEvidenceAsync(db, alert, cancellationToken);
        logger.LogWarning(
            "ANOMALY: {Id}. Requesting {Advisor} diagnosis...",
            alert.EquipmentId,
            advisor.Name
        );

        string diagnosis;
        using var adviceTimeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        adviceTimeout.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            diagnosis = Truncate(
                await advisor.DiagnoseAsync(evidence, adviceTimeout.Token),
                8_000
            );
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A failed or slow LLM still yields useful, deterministic text rather than a bare
            // "unavailable"; the metric keeps the degradation visible.
            logger.LogError(exception, "{Advisor} diagnosis failed", advisor.Name);
            metrics.AiFailures.Add(1);
            diagnosis = "AI unavailable. " + RuleBasedDiagnosisAdvisor.Diagnose(evidence);
        }

        return JsonSerializer.Serialize(alert with { Diagnostics = diagnosis });
    }

    /// <summary>
    /// Reads the committed anomaly row and the equipment's preceding readings. Both queries use
    /// the (EquipmentId, OccurredAt) and MessageId indexes. A missing row (pruned or hand-made
    /// outbox entry) still yields evidence from the alert itself.
    /// </summary>
    internal static async Task<DiagnosisEvidence> LoadEvidenceAsync(
        AppDbContext db,
        TelemetryAlertEnvelope alert,
        CancellationToken cancellationToken
    )
    {
        TelemetryReading? reading = Guid.TryParse(alert.MessageId, out var messageId)
            ? await db
                .TelemetryReadings.AsNoTracking()
                .SingleOrDefaultAsync(r => r.MessageId == messageId, cancellationToken)
            : null;

        var before = reading?.OccurredAt ?? alert.OccurredAt;
        var recent = await db
            .TelemetryReadings.AsNoTracking()
            .Where(r => r.EquipmentId == alert.EquipmentId && r.OccurredAt < before)
            .OrderByDescending(r => r.OccurredAt)
            .Take(DiagnosisEvidence.RecentWindow)
            .Select(r => r.EngineTemperature)
            .ToListAsync(cancellationToken);
        recent.Reverse();

        return new DiagnosisEvidence(
            alert.EquipmentId,
            alert.OccurredAt,
            alert.EngineTemperature,
            reading?.OilPressure,
            reading?.DetectorScore,
            reading?.DetectorPValue,
            reading?.DetectorHistoryCount,
            recent
        );
    }

    internal static bool TryReadPendingEnrichment(
        string payload,
        out TelemetryAlertEnvelope? alert
    )
    {
        try
        {
            alert = JsonSerializer.Deserialize<TelemetryAlertEnvelope>(payload);

            // Enrichment rewrites the payload, so it runs only on a pending marker (empty
            // Diagnostics) whose identity fields survived deserialization. Anything else is
            // published unchanged. A payload whose keys do not bind, such as one with lowercase
            // keys, deserializes with a null MessageId, and re-serializing it would lose the ID.
            return alert is not null
                && string.IsNullOrEmpty(alert.Diagnostics)
                && !string.IsNullOrEmpty(alert.MessageId)
                && !string.IsNullOrEmpty(alert.EquipmentId);
        }
        catch (JsonException)
        {
            // This process writes valid JSON, but a hand-edited row might not parse. Publish it
            // unchanged so one bad row does not block the outbox.
            alert = null;
            return false;
        }
    }

    private async Task BackoffAsync(CancellationToken cancellationToken)
    {
        _consecutiveFailures++;
        var exponential = _baseBackoff * Math.Pow(2, Math.Min(_consecutiveFailures - 1, 10));
        var capped = exponential > _maxBackoff ? _maxBackoff : exponential;
        var jittered = TimeSpan.FromMilliseconds(
            Random.Shared.NextDouble() * capped.TotalMilliseconds
        );
        await SafeDelayAsync(jittered, cancellationToken);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static async Task SafeDelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private enum PublishOutcome
    {
        Empty,
        Enriched,
        Published,
        Failed,
    }
}
