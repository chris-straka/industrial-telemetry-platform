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
/// Delivery is ALO, ambiguous failures re-send, and cloud dedupes via MessageId (EffO)
/// We send oldest-first ID and delete only what AcceptedCount confirms
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
    // How many records to move per request (bigger -> fewer round trips, more resent)
    private readonly int _batchSize = uploaderOptions.Value.BatchSize;

    // Poll interval for when the buffer is empty (it usually is, fresh readings wait this)
    private readonly TimeSpan _idleDelay = TimeSpan.FromMilliseconds(
        uploaderOptions.Value.IdleDelayMs
    );

    // Base wait time for each retry, doubled per consecutive failure
    private readonly TimeSpan _baseBackoff = TimeSpan.FromSeconds(
        uploaderOptions.Value.BaseBackoffSeconds
    );

    // Where the doubling stops, keeps retrying instead of drifting to hours
    private readonly TimeSpan _maxBackoff = TimeSpan.FromSeconds(
        uploaderOptions.Value.MaxBackoffSeconds
    );

    // How long a batch gets (caps the wait on a hung connection)
    private readonly TimeSpan _uploadTimeout = TimeSpan.FromSeconds(
        uploaderOptions.Value.UploadTimeoutSeconds
    );

    // Failures since the last accepted batch, which resets it to 0
    private int _consecutiveFailures;

    // What decides whether an outage logs loudly or quietly
    private int _consecutiveUnreachable;

    // Used to find records the cloud won't take
    private bool _sendOneAtATime;

    // Rejected readings dropped in a row (past the cap, blame the cloud and stop dropping)
    private int _consecutiveDrops;
    private const int MaxConsecutiveDrops = 10;

    // Passes left before depth gauge is rebuilt from COUNT(*)
    private int _passesUntilDepthResync;
    private const int DepthResyncInterval = 100;
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
                // Type created by grpc_csharp_plugin (with .proto's service)
                var grpcClient =
                    scope.ServiceProvider.GetRequiredService<TelemetryIngestion.TelemetryIngestionClient>();

                // Refreshes depth values for Otel (important during cloud outage)
                await RefreshGaugesAsync(db, stoppingToken);

                // SQLite assigns IDs in insert order (lower ID -> older)
                var pending = await db
                    .TelemetryRecords.OrderBy(r => r.Id) // oldest -> youngest
                    .Take(_sendOneAtATime ? 1 : _batchSize) // take first N rows
                    .ToListAsync(stoppingToken);

                if (pending.Count == 0)
                {
                    // nothing to send, wait
                    await SafeDelayAsync(_idleDelay, stoppingToken);
                    continue;
                }

                // Draining oldest-first means an unsendable row stalls new readings
                // I handle it by dropping & logging stalled rows (aka poison msgs)
                var batch = new List<PendingUpload>(pending.Count);
                var poisoned = new List<TelemetryRecord>();

                foreach (var item in pending)
                {
                    try
                    {
                        batch.Add(new PendingUpload(item, ToReading(item)));
                    }
                    catch (Exception ex)
                    {
                        // Request obj malformed (nulls, range violation)
                        logger.LogError(
                            ex,
                            "Dropping unsendable reading {MessageId} from {EquipmentId}.",
                            item.MessageId,
                            item.EquipmentId
                        );
                        poisoned.Add(item);
                    }
                }

                // Remove poisoned msgs
                if (poisoned.Count > 0)
                {
                    db.TelemetryRecords.RemoveRange(poisoned);
                    await db.SaveChangesAsync(stoppingToken);
                    metrics.Poisoned.Add(poisoned.Count);
                    bufferDepth.Decrement(poisoned.Count);
                }

                if (batch.Count == 0)
                    continue;

                var result = await UploadBatchAsync(grpcClient, batch, stoppingToken);

                // Reachable means the cloud answered, whatever it answered with
                // Set here rather than in each arm below, so the four outcomes cannot disagree about it
                metrics.SetCloudReachable(result.Outcome != UploadOutcome.Unreachable);

                // No default arm on purpose
                // A fifth outcome should fall through to the delete below and be caught in review
                // rather than compile into a silent case
                switch (result.Outcome)
                {
                    case UploadOutcome.Malformed:
                        await HandleRejectedBatchAsync(db, batch, stoppingToken);
                        continue;

                    // Both already logged themselves, and neither leaves anything safe to delete
                    case UploadOutcome.Unreachable:
                    case UploadOutcome.Refused:
                        await BackoffAsync(stoppingToken);
                        continue;

                    // The cloud took none of them, so there is nothing to delete and no reason to hammer it
                    case UploadOutcome.Answered when result.AcceptedReadings <= 0:
                        await BackoffAsync(stoppingToken);
                        continue;
                }

                // Delete ONLY what the cloud confirmed
                // It will retry failed reqs in the next pass
                // Dying between the ACK and this delete will resend the entire batch (all dupes)
                db.TelemetryRecords.RemoveRange(
                    batch.Take(result.AcceptedReadings).Select(p => p.Record)
                );
                await db.SaveChangesAsync(stoppingToken);

                metrics.Uploaded.Add(result.AcceptedReadings);
                bufferDepth.Decrement(result.AcceptedReadings);
                _consecutiveFailures = 0;
                _consecutiveUnreachable = 0;
                _sendOneAtATime = false;
                _consecutiveDrops = 0;

                if (result.AcceptedReadings < batch.Count)
                {
                    logger.LogWarning(
                        "Cloud accepted {Accepted}/{Sent}. Retrying the remainder.",
                        result.AcceptedReadings,
                        batch.Count
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
                // A bug or a local failure (disk full, corrupt database)
                // But not the cloud, outages are handled in UploadBatchAsync()
                logger.LogError(ex, "Unexpected failure in the uploader loop.");
                await BackoffAsync(stoppingToken);
            }
        }

        logger.LogInformation("Edge uploader stopped.");
    }

    private async Task<UploadResult> UploadBatchAsync(
        TelemetryIngestion.TelemetryIngestionClient grpcClient,
        List<PendingUpload> batch,
        CancellationToken cancellationToken
    )
    {
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

            var response = await call.ResponseAsync;

            var acceptedReadings = response.Success ? response.AcceptedCount : 0;
            activity?.SetTag("edge.batch.accepted", acceptedReadings);
            return new UploadResult(UploadOutcome.Answered, acceptedReadings);
        }
        catch (RpcException ex) when (IsShutdownCancellation(ex.StatusCode, cancellationToken))
        {
            // gRPC reports cancellation as an RpcException, we change it to fit our contract
            throw new OperationCanceledException(cancellationToken);
        }
        catch (RpcException ex) when (IsBatchContentError(ex.StatusCode))
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            metrics.RecordUploadFailure("malformed");
            return new UploadResult(UploadOutcome.Malformed, 0);
        }
        catch (RpcException ex) when (IsCallerRefused(ex.StatusCode))
        {
            // Cloud refuses caller (only a config change can fix)
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            metrics.RecordUploadFailure("refused");
            logger.LogError(
                ex,
                "Cloud refused the upload with {Status}. Buffering until this is fixed.",
                ex.StatusCode
            );
            return new UploadResult(UploadOutcome.Refused, 0);
        }
        catch (RpcException ex)
        {
            // Covers a failed connection, an expired deadline, and every status not singled out above
            // A local bug is not in here, it belongs to the loop's handler
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            metrics.RecordUploadFailure("unreachable");

            // Log the first failure loudly, then quietly
            // A 30 minute outage should not produce 30 minutes of identical ERROR lines
            _consecutiveUnreachable++;

            if (_consecutiveUnreachable == 1)
                logger.LogWarning(ex, "Cloud unreachable. Buffering locally.");
            else
                logger.LogDebug(
                    "Cloud still unreachable ({Attempts} attempts).",
                    _consecutiveUnreachable
                );

            return new UploadResult(UploadOutcome.Unreachable, 0);
        }
    }

    /// <summary>
    /// Handles a batch the cloud answered and refused on content.
    /// </summary>
    /// <remarks>
    /// One record per pass finds the offender, and only that record is dropped
    /// The alternative, dropping the whole batch on the first rejection, throws away 199 readings the cloud never objected to
    ///
    /// Capped, because a cloud that calls everything invalid would otherwise empty the buffer one record per round trip
    /// Past the cap this holds the queue instead, which is the failure an operator can still fix
    /// </remarks>
    private async Task HandleRejectedBatchAsync(
        EdgeDbContext db,
        List<PendingUpload> batch,
        CancellationToken cancellationToken
    )
    {
        if (batch.Count > 1)
        {
            _sendOneAtATime = true;
            return;
        }

        if (_consecutiveDrops >= MaxConsecutiveDrops)
        {
            logger.LogError(
                "Cloud rejected {Drops} readings in a row. Holding {MessageId} and everything behind it.",
                _consecutiveDrops,
                batch[0].Record.MessageId
            );
            await BackoffAsync(cancellationToken);
            return;
        }

        logger.LogError(
            "Cloud rejected reading {MessageId} from {EquipmentId} as invalid. Dropping it.",
            batch[0].Record.MessageId,
            batch[0].Record.EquipmentId
        );

        db.TelemetryRecords.Remove(batch[0].Record);
        await db.SaveChangesAsync(cancellationToken);
        metrics.Poisoned.Add(1);
        bufferDepth.Decrement(1);

        _consecutiveDrops++;
        _sendOneAtATime = false;
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
    /// ActivityContext = SpanContext (TraceId, SpanId, TraceFlags, TraceState, IsRemote)
    ///
    /// Traceparent = TraceId, SpanId, TraceFlags
    ///
    /// TraceState is for 3rd party vendors like Datadog, New Relic (not relevant 4 me)
    ///
    /// Parse would throw for the entire batch unlike TryParse
    /// </remarks>
    /// <param name="batch">Split into SQLite rows and sensor readings built from them</param>
    /// <returns>List of links with trace information</returns>
    private static List<ActivityLink> BuildTraceLinks(List<PendingUpload> batch) =>
        batch
            .Select(p => p.Record.TraceParent)
            .Where(tp => !string.IsNullOrEmpty(tp))
            // Remote because these arrived from the sensor over HTTP, they were not made here
            .Select(tp =>
                ActivityContext.TryParse(tp, null, isRemote: true, out var ctx) ? ctx : default
            )
            .Where(ctx => ctx != default)
            .Select(ctx => new ActivityLink(ctx))
            .ToList();

    /// <summary>
    /// Updates the buffer state so the gauges have fresh data.
    /// </summary>
    /// <remarks>
    /// On a schedule rather than every pass, because COUNT(*) walks every row
    /// Draining a full buffer is thousands of passes
    /// Paying for a scan on each one is not worth the gateway's CPU
    /// </remarks>
    private async Task RefreshGaugesAsync(EdgeDbContext db, CancellationToken cancellationToken)
    {
        _passesUntilDepthResync--;

        if (_passesUntilDepthResync <= 0)
        {
            bufferDepth.SetTo(await db.TelemetryRecords.CountAsync(cancellationToken));
            _passesUntilDepthResync = DepthResyncInterval;
        }

        if (bufferDepth.Current == 0)
        {
            metrics.SetOldestAgeSeconds(0);
            return;
        }

        // This can run every pass because it grabs one row off the PK
        // Nullable because DB could be empty (cached depth != DB depth)
        var oldest = await db
            .TelemetryRecords.OrderBy(r => r.Id)
            .Select(r => (DateTimeOffset?)r.OccurredAt)
            .FirstOrDefaultAsync(cancellationToken);

        // If DB is empty
        if (oldest is null)
        {
            metrics.SetOldestAgeSeconds(0);
            return;
        }

        metrics.SetOldestAgeSeconds((DateTimeOffset.UtcNow - oldest.Value).TotalSeconds);
    }

    /// <summary>
    /// Exponential backoff with full jitter.
    /// </summary>
    /// <remarks>
    /// Jittered because every gateway in the fleet fails on the same cadence in an outage
    /// An unjittered wait reconnects them all at once and knocks the cloud back over
    ///
    /// Hand-rolled rather than Polly, which the emulator uses one hop down
    /// The wait is sized by loop state that outlives any one call, and it is entered from three different decisions
    /// That makes it this component's state machine, not a policy wrapped around a call
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

    // The cloud answered, so it is reachable and this batch's contents are the problem
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

    private readonly record struct UploadResult(UploadOutcome Outcome, int AcceptedReadings);

    // A row paired with the reading built from it, rather than two lists walked by the same index
    // Deleting the first AcceptedCount rows is only correct while row i produced reading i
    // Pairing them makes that structural instead of a convention
    private readonly record struct PendingUpload(TelemetryRecord Record, TelemetryReading Reading);

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
