using System.Diagnostics;

using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using Industrial.Ingestion.Api; // generated from Protos/telemetry.proto
using Industrial.Sensor.EdgeGateway.Configuration;
using Industrial.Sensor.EdgeGateway.Features.Buffer;
using Industrial.Sensor.EdgeGateway.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Industrial.Sensor.EdgeGateway.Features.Upload;

/// <summary>
/// Drains the local SQLite buffer to the cloud over gRPC.
/// </summary>
/// <remarks>
/// This loop runs independently of the receiver endpoint. It sends readings oldest first and
/// deletes only the ones the cloud names in its response.
///
/// Delivery is at-least-once, and ambiguous failures re-send. Postgres deduplicates on MessageId,
/// and the live dashboard keeps its own bounded duplicate window.
///
/// gRPC's built-in retry policy is not used because it would re-send the whole batch without
/// knowing which readings persisted.
/// </remarks>
public class UploaderWorker(
    IServiceScopeFactory scopeFactory,
    EdgeMetrics metrics,
    BufferDepth bufferDepth,
    IOptions<UploaderOptions> uploaderOptions,
    ILogger<UploaderWorker> logger
) : BackgroundService
{
    #region private_vars

    // Larger batches mean fewer round trips but more readings re-sent after a failure.
    private readonly int _batchSize = uploaderOptions.Value.BatchSize;

    // Poll interval when the buffer is empty, which is usually how long a new reading waits.
    private readonly TimeSpan _idleDelay = TimeSpan.FromMilliseconds(
        uploaderOptions.Value.IdleDelayMs
    );

    // Doubled for each consecutive failure.
    private readonly TimeSpan _baseBackoff = TimeSpan.FromSeconds(
        uploaderOptions.Value.BaseBackoffSeconds
    );

    private readonly TimeSpan _maxBackoff = TimeSpan.FromSeconds(
        uploaderOptions.Value.MaxBackoffSeconds
    );

    // Caps the wait on a hung connection.
    private readonly TimeSpan _uploadTimeout = TimeSpan.FromSeconds(
        uploaderOptions.Value.UploadTimeoutSeconds
    );

    // Failures since the last accepted batch.
    private int _consecutiveFailures;

    // Decides whether an outage logs at warning or debug level.
    private int _consecutiveUnreachable;

    #endregion

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Edge uploader started. Batch size {BatchSize}, idle poll {IdleDelay}.",
            _batchSize,
            _idleDelay
        );

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // A BackgroundService lives for the whole process, so each pass makes a scope
                // for its scoped dependencies.
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<EdgeDbContext>();
                var settlementStore =
                    scope.ServiceProvider.GetRequiredService<BufferSettlementStore>();
                var grpcClient =
                    scope.ServiceProvider.GetRequiredService<TelemetryIngestion.TelemetryIngestionClient>();

                await RefreshGaugesAsync(db, stoppingToken);

                // SQLite assigns rowids in insert order, so the lowest Id is the oldest reading.
                var pending = await db
                    .TelemetryRecords.OrderBy(r => r.Id)
                    .Take(_batchSize)
                    .ToListAsync(stoppingToken);

                if (pending.Count == 0)
                {
                    await SafeDelayAsync(_idleDelay, stoppingToken);
                    continue;
                }

                var batch = pending
                    .Select(item => new OutgoingReading(item, ToReading(item)))
                    .ToList();

                var result = await UploadBatchAsync(grpcClient, batch, stoppingToken);

                metrics.SetCloudReachable(result.Outcome != UploadOutcome.Unreachable);

                switch (result.Outcome)
                {
                    // Each of these already logged, and none makes any row safe to delete.
                    case UploadOutcome.Malformed:
                    case UploadOutcome.Unreachable:
                    case UploadOutcome.Refused:
                        await BackoffAsync(stoppingToken);
                        continue;

                    // Nothing was settled, so back off rather than resend immediately.
                    case UploadOutcome.Answered when result.Settled.Count == 0:
                        await BackoffAsync(stoppingToken);
                        continue;
                }

                var settledRows = batch
                    .Where(p => result.Settled.Contains(p.Record.MessageId))
                    .Select(p => p.Record)
                    .ToList();

                await settlementStore.SettleAsync(
                    settledRows,
                    result.RejectedIds,
                    stoppingToken
                );

                metrics.Uploaded.Add(result.AcceptedIds.Count);
                metrics.Rejected.Add(result.RejectedIds.Count);

                _consecutiveFailures = 0;
                _consecutiveUnreachable = 0;

                if (result.RejectedIds.Count > 0)
                {
                    logger.LogError(
                        "Cloud permanently rejected {Count} readings. Quarantined {MessageIds} "
                            + "with a generic reason because the response has no per-reading cause.",
                        result.RejectedIds.Count,
                        string.Join(", ", result.RejectedIds)
                    );
                }

                if (settledRows.Count < batch.Count)
                {
                    logger.LogWarning(
                        "Cloud settled {Settled}/{Sent} (faulted: {Faulted}). Retrying the remainder.",
                        settledRows.Count,
                        batch.Count,
                        !result.Complete
                    );
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A bug or a local failure such as a full disk. UploadBatchAsync classifies cloud
                // failures itself, so they do not reach this handler.
                logger.LogError(ex, "Unexpected failure in the uploader loop.");
                await BackoffAsync(stoppingToken);
            }
        }

        logger.LogInformation("Edge uploader stopped.");
    }

    private async Task<UploadResult> UploadBatchAsync(
        TelemetryIngestion.TelemetryIngestionClient grpcClient,
        List<OutgoingReading> batch,
        CancellationToken cancellationToken
    )
    {
        var startedAt = Stopwatch.GetTimestamp();
        var outcome = "local_failure";

        // Each sensor trace ended at the gateway's 202. Linking them lets the batch trace
        // reference every reading it carries.
        var links = BuildTraceLinks(batch);

        // Null when nothing listens to the source.
        using var activity = EdgeTracing.Source.StartActivity(
            "edge.upload",
            ActivityKind.Client,
            parentContext: default,
            tags:
            [
                new("edge.batch.size", batch.Count),
                new("edge.batch.linked_traces", links.Count),
                new("edge.upload.attempt", _consecutiveFailures + 1),
            ],
            links: links
        );

        try
        {
            var request = new UploadTelemetryRequest
            {
                Readings = { batch.Select(p => p.Reading) },
            };

            activity?.SetTag("edge.batch.bytes", request.CalculateSize());

            using var call = grpcClient.UploadTelemetryAsync(
                request,
                deadline: DateTime.UtcNow.Add(_uploadTimeout),
                cancellationToken: cancellationToken
            );

            // The call is disposable but its response is not, so await it separately.
            var response = await call.ResponseAsync;
            outcome = "answered";

            activity?.SetTag("edge.batch.accepted", response.AcceptedMessageIds.Count);
            activity?.SetTag("edge.batch.rejected", response.RejectedMessageIds.Count);

            var sentIds = batch
                .Select(item => item.Record.MessageId)
                .ToHashSet(StringComparer.Ordinal);
            var acceptedIds = response.AcceptedMessageIds.ToHashSet(StringComparer.Ordinal);
            var rejectedIds = response.RejectedMessageIds.ToHashSet(StringComparer.Ordinal);

            if (
                !acceptedIds.IsSubsetOf(sentIds)
                || !rejectedIds.IsSubsetOf(sentIds)
                || acceptedIds.Overlaps(rejectedIds)
            )
            {
                outcome = "malformed_response";
                activity?.SetStatus(
                    ActivityStatusCode.Error,
                    "Cloud response contained an unknown or contradictory MessageId."
                );
                metrics.RecordUploadFailure("malformed_response");
                logger.LogError(
                    "Cloud returned an invalid acknowledgement set. No local rows will be deleted."
                );
                return UploadResult.Failed(UploadOutcome.Malformed);
            }

            return new UploadResult(
                UploadOutcome.Answered,
                [.. acceptedIds],
                [.. rejectedIds],
                response.Success
            );
        }
        catch (RpcException ex) when (IsShutdownCancellation(ex.StatusCode, cancellationToken))
        {
            outcome = "cancelled";
            // gRPC reports cancellation as an RpcException. Rethrow it as the standard
            // cancellation exception the loop handles.
            throw new OperationCanceledException(cancellationToken);
        }
        catch (RpcException ex) when (IsBatchContentError(ex.StatusCode))
        {
            outcome = "malformed";
            // The cloud refused the call because of its content.
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            metrics.RecordUploadFailure("malformed");
            logger.LogError(
                ex,
                "Cloud refused the whole batch with {Status}. Buffering until this is fixed.",
                ex.StatusCode
            );
            return UploadResult.Failed(UploadOutcome.Malformed);
        }
        catch (RpcException ex) when (IsCallerRefused(ex.StatusCode))
        {
            outcome = "refused";
            // The cloud refused this gateway. Only a configuration change can fix it.
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            metrics.RecordUploadFailure("refused");
            logger.LogError(
                ex,
                "Cloud rejected this gateway with {Status}. Buffering until this is fixed.",
                ex.StatusCode
            );
            return UploadResult.Failed(UploadOutcome.Refused);
        }
        catch (RpcException ex)
        {
            outcome = "unreachable";
            // A dropped connection or an expired deadline.
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            metrics.RecordUploadFailure("unreachable");

            _consecutiveUnreachable++;

            if (_consecutiveUnreachable == 1)
                logger.LogWarning(ex, "Cloud unreachable. Buffering locally.");
            else
                logger.LogDebug(
                    "Cloud still unreachable ({Attempts} attempts).",
                    _consecutiveUnreachable
                );

            return UploadResult.Failed(UploadOutcome.Unreachable);
        }
        finally
        {
            metrics.RecordUploadDuration(
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                outcome
            );
        }
    }

    private static TelemetryReading ToReading(TelemetryRecord item) =>
        new()
        {
            MessageId = item.MessageId,
            EquipmentId = item.EquipmentId,
            SequenceNumber = item.SequenceNumber,
            OccurredAt = Timestamp.FromDateTimeOffset(item.OccurredAt),
            EngineTemperature = item.EngineTemperature,
            OilPressure = item.OilPressure,
            Traceparent = item.TraceParent ?? "",
        };

    /// <summary>
    /// Builds one link per stored sensor trace context so the batch span can reference them.
    /// </summary>
    /// <remarks>
    /// TryParse skips a malformed traceparent. Parse would throw and fail the whole batch.
    /// </remarks>
    private static List<ActivityLink> BuildTraceLinks(List<OutgoingReading> batch) =>
        batch
            .Select(p => p.Record.TraceParent)
            .Where(tp => !string.IsNullOrEmpty(tp))
            .Select(tp =>
                ActivityContext.TryParse(tp, null, isRemote: true, out var ctx) ? ctx : default
            )
            .Where(ctx => ctx != default)
            .Select(ctx => new ActivityLink(ctx))
            .ToList();

    /// <summary>
    /// Updates gauges derived from the oldest queued row.
    /// </summary>
    /// <remarks>
    /// BufferDepth is initialized from COUNT(*) once at startup and then maintained by atomic
    /// reservations. Recounting here would race those reservations and overwrite a newer value.
    /// </remarks>
    private async Task RefreshGaugesAsync(EdgeDbContext db, CancellationToken cancellationToken)
    {
        if (bufferDepth.Current == 0)
        {
            metrics.NoReadingsBuffered();
            return;
        }

        // Reads one row off the primary key, so it is cheap enough to run every pass.
        var oldest = await db
            .TelemetryRecords.OrderBy(r => r.Id)
            .Select(r => (DateTimeOffset?)r.OccurredAt) // null, not 0001-01-01, when empty
            .FirstOrDefaultAsync(cancellationToken);

        // BufferDepth counts a reservation before its insert commits, so the table can still be
        // empty.
        if (oldest is null)
        {
            metrics.NoReadingsBuffered();
            return;
        }

        metrics.SetOldestReadingAge((DateTimeOffset.UtcNow - oldest.Value).TotalSeconds);
    }

    /// <summary>
    /// Exponential backoff with full jitter.
    /// </summary>
    /// <remarks>
    /// Jitter stops every gateway from retrying on the same cadence during an outage. Polly is not
    /// used because this backs off loop iterations, not a single call.
    /// </remarks>
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

    // Our own shutdown cancelled the call, rather than the server.
    private static bool IsShutdownCancellation(StatusCode status, CancellationToken token) =>
        status == StatusCode.Cancelled && token.IsCancellationRequested;

    // The cloud is reachable and refused the call as a whole. Per-reading rejections arrive in a
    // successful response instead.
    private static bool IsBatchContentError(StatusCode status) =>
        status is StatusCode.InvalidArgument or StatusCode.OutOfRange;

    // The cloud is up and refusing this caller, so changing the batch cannot help.
    private static bool IsCallerRefused(StatusCode status) =>
        status
            is StatusCode.Unauthenticated
                or StatusCode.PermissionDenied
                or StatusCode.Unimplemented;

    // The loop needs a different decision for each case, which a single rethrown exception
    // could not express.
    private enum UploadOutcome
    {
        Answered,
        Unreachable,
        Malformed,
        Refused,
    }

    /// <param name="Outcome">How the call ended.</param>
    /// <param name="AcceptedIds">Readings the cloud holds durably.</param>
    /// <param name="RejectedIds">Readings the cloud rejected permanently.</param>
    /// <param name="Complete">False when the cloud left some readings unsettled.</param>
    private readonly record struct UploadResult(
        UploadOutcome Outcome,
        IReadOnlyList<string> AcceptedIds,
        IReadOnlyList<string> RejectedIds,
        bool Complete
    )
    {
        public HashSet<string> Settled { get; } = [.. AcceptedIds.Concat(RejectedIds)];

        public static UploadResult Failed(UploadOutcome outcome) =>
            new(outcome, [], [], Complete: false);
    }

    private readonly record struct OutgoingReading(
        TelemetryRecord Record,
        TelemetryReading Reading
    );

    // Task.Delay throws on cancellation, and one caller is the loop's catch block. A throw from
    // there would escape ExecuteAsync instead of reaching the shutdown handler.
    private static async Task SafeDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }
}
