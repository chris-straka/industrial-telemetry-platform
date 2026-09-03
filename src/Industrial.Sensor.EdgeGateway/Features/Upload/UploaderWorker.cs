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
/// THE SENDER. Drains local SQLite buffer to send readings to the cloud over gRPC.
/// </summary>
/// <remarks>
/// This loop is independent from acquisition (receiver endpoint)
///
/// Delivery is ALO and ambiguous failures re-send. Diagnostics/Postgres dedupes MessageId, while
/// the live dashboard keeps its own bounded duplicate window.
/// We send oldest-first ID and delete only the readings the cloud named in its response
///
/// Doesn't use gRPC's built-in retry policy
/// It would re-send everything without knowing what persisted
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

    // How many readings to send to the cloud per request
    // The more readings -> fewer round trips -> more readings resent on failures
    private readonly int _batchSize = uploaderOptions.Value.BatchSize;

    // How often we check SQLite for new readings to send
    // It's usually empty, so it's usually how long new readings wait b4 being sent
    private readonly TimeSpan _idleDelay = TimeSpan.FromMilliseconds(
        uploaderOptions.Value.IdleDelayMs
    );

    // Base wait time for each backoff, doubled for each consecutive failure
    private readonly TimeSpan _baseBackoff = TimeSpan.FromSeconds(
        uploaderOptions.Value.BaseBackoffSeconds
    );

    // Where the doubling stops (we don't want it to wait for hours)
    private readonly TimeSpan _maxBackoff = TimeSpan.FromSeconds(
        uploaderOptions.Value.MaxBackoffSeconds
    );

    // How long a batch gets (caps the wait on a hung connection)
    private readonly TimeSpan _uploadTimeout = TimeSpan.FromSeconds(
        uploaderOptions.Value.UploadTimeoutSeconds
    );

    // Failures since the last accepted batch (resets to 0)
    private int _consecutiveFailures;

    // What decides whether an outage logs loudly or quietly
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
                // We're in a BackgroundService (process liftime) so we need scoped deps
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<EdgeDbContext>();
                var settlementStore =
                    scope.ServiceProvider.GetRequiredService<BufferSettlementStore>();
                // Type created by grpc_csharp_plugin (with .proto's service)
                var grpcClient =
                    scope.ServiceProvider.GetRequiredService<TelemetryIngestion.TelemetryIngestionClient>();

                // Refreshes depth values for Otel (cloud outage visibility)
                await RefreshGaugesAsync(db, stoppingToken);

                // SQLite assigns IDs in insert order (lower ID -> older)
                var pending = await db
                    .TelemetryRecords.OrderBy(r => r.Id) // oldest -> youngest
                    .Take(_batchSize) // take first N rows
                    .ToListAsync(stoppingToken);

                if (pending.Count == 0)
                {
                    // nothing to send, wait
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
                    // All three logged themselves, and none leaves anything safe to delete
                    case UploadOutcome.Malformed:
                    case UploadOutcome.Unreachable:
                    case UploadOutcome.Refused:
                        await BackoffAsync(stoppingToken);
                        continue;

                    // The cloud settled none of them
                    // nothing to delete and no reason to hammer it
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
                // Normal shutdown, not a cloud outage
                break;
            }
            catch (Exception ex)
            {
                // A bug or a local failure (disk full, corrupt DB)
                // But not the cloud, outages are handled earlier in UploadBatchAsync()
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

        // Gateway returns 202 and breaks incoming sensor traces
        // This fans those traces in so the new batch trace can ref them
        var links = BuildTraceLinks(batch);

        // Create a new Activity (aka span in .NET)
        // Activity is null if nothing listens to the source
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
            // Create a new request for the batch
            var request = new UploadTelemetryRequest
            {
                Readings = { batch.Select(p => p.Reading) }, // don't include DB rows
            };

            activity?.SetTag("edge.batch.bytes", request.CalculateSize());

            using var call = grpcClient.UploadTelemetryAsync(
                request,
                deadline: DateTime.UtcNow.Add(_uploadTimeout), // if cloud hangs
                cancellationToken: cancellationToken
            );

            // TelemetryResponse isn't disposable, I couldn't await + using
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
            // gRPC reports cancellation as an RpcException, we change it to fit our contract
            throw new OperationCanceledException(cancellationToken);
        }
        catch (RpcException ex) when (IsBatchContentError(ex.StatusCode))
        {
            outcome = "malformed";
            // Cloud refuses call based on its content
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
            // Cloud refuses caller (only a config change can fix)
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
            // A dropped connection or an expired deadline lands here
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

    // Changing the EF Type to the protobuf type
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
    /// Grabs the OTel traces from all requests in the current batch
    /// Then it fans them all into one trace to send to the cloud
    /// </summary>
    /// <remarks>
    ///
    /// ActivityContext = SpanContext = Context (Traceparent, TraceState, IsRemote)
    /// Traceparent = TraceId, SpanId, TraceFlags
    /// TraceState = 3rd party vendors e.g, Datadog, New Relic
    /// isRemote = true when the context came from the wire
    ///
    /// Parse() would throw for the entire batch unlike TryParse()
    /// </remarks>
    /// <param name="batch">Split into SQLite rows and sensor readings built from them</param>
    /// <returns>List of links with trace information</returns>
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

        // This is fast because it grabs one row off the PK (can run every pass)
        var oldest = await db
            .TelemetryRecords.OrderBy(r => r.Id)
            .Select(r => (DateTimeOffset?)r.OccurredAt) // changes default to null (not 0001-01-01)
            .FirstOrDefaultAsync(cancellationToken);

        // DB is actually empty while our buffer is non-empty (b4 a sync)
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
    /// Jittered otherwise every gateway fails @ the same cadence during an outage
    ///
    /// Hand-rolled rather than Polly because the backoff is for loop iterations, not one call
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

    // Our own shutdown cancelled the call, rather than the server hanging up on us
    private static bool IsShutdownCancellation(StatusCode status, CancellationToken token) =>
        status == StatusCode.Cancelled && token.IsCancellationRequested;

    // The cloud answered, so it is reachable and it is refusing the CALL, not readings within it
    // Per-reading rejections come back in the response, which is a success
    private static bool IsBatchContentError(StatusCode status) =>
        status is StatusCode.InvalidArgument or StatusCode.OutOfRange;

    // The cloud is up and refusing this caller, so reshaping the batch cannot help
    private static bool IsCallerRefused(StatusCode status) =>
        status
            is StatusCode.Unauthenticated
                or StatusCode.PermissionDenied
                or StatusCode.Unimplemented;

    // A classification rather than letting the exception reach the loop, because the four cases need four different decisions
    // One rethrow would flatten them back into "something failed", which is the bug this fixes
    private enum UploadOutcome
    {
        Answered,
        Unreachable,
        Malformed,
        Refused,
    }

    /// <param name="Outcome">Which way the call ended</param>
    /// <param name="AcceptedIds">Readings the cloud durably holds</param>
    /// <param name="RejectedIds">Readings the cloud will refuse forever</param>
    /// <param name="Complete">False when the cloud stopped partway</param>
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

    // Task.Delay throws when the token trips, and one caller is the loop's catch block
    // A throw from there escapes ExecuteAsync instead of reaching the shutdown handler
    private static async Task SafeDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }
}
