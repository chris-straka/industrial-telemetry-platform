# Upload failures: three questions, not one

When an upload does not fully succeed, the drain loop needs three separate answers:

- Is the cloud reachable? (drives `edge.cloud.reachable` and the backoff)
- Will this batch *ever* be accepted? (drives whether retrying it is progress or a stall)
- What may be deleted from SQLite? (drives the invariant)

A single `catch (RpcException)` answers none of them. It answers "something went wrong",
which is the same thing it says when the network is down, when the batch is malformed,
and when the API key expired -- three situations whose correct responses have nothing in
common.

# Why a returned classification and not an exception

`UploadBatchAsync` returns `UploadResult(UploadOutcome, AcceptedIds, RejectedIds, Complete)`
instead of throwing.

Exceptions are for conditions the current frame cannot handle. The uploader can handle
all of these -- surviving a dead cloud is the entire reason this process exists -- so an
outage is not exceptional here, it is the expected steady state during the demo.

The pattern is ordinary in production code. `SignInResult` (Success / LockedOut /
RequiresTwoFactor / NotAllowed), Confluent's `PersistenceStatus`, Polly's `Outcome<T>`,
and gRPC's own `StatusCode` all exist because the caller's next move differs per case.

**Rejected: catch `RpcException` in `ExecuteAsync` and switch on `ex.StatusCode` there.**
Works, and it is less code. But it puts gRPC vocabulary in the drain loop, so the loop
would have to know that `Unimplemented` means "keep buffering, a human must fix this".
The classification is the seam: `UploadBatchAsync` is the only thing that speaks gRPC,
`ExecuteAsync` speaks buffer policy. That line is what lets the transport change without
the loop changing.

**Rejected: a single `bool retryable`.** It cannot express the difference between "retry
this batch" and "retry, but never this batch's *contents*" -- and that difference is
precisely the stall this classification was added to fix.

# The four buckets

| outcome | statuses | loop does | `cloud.reachable` |
| --- | --- | --- | --- |
| `Answered` | none (normal return) | delete every id the cloud named, retry the rest | 1 |
| `Unreachable` | `Unavailable`, `DeadlineExceeded`, `Internal`, everything unlisted | backoff, retry forever | 0 |
| `Malformed` | `InvalidArgument`, `OutOfRange` | log loudly, buffer forever, drop nothing | 1 |
| `Refused` | `Unauthenticated`, `PermissionDenied`, `Unimplemented` | log loudly, buffer forever, drop nothing | 1 |

The last two leave the gauge at 1 on purpose. The cloud answered; a rejection is proof of
reachability, and an operator paged for "cloud unreachable" would go looking at the wrong
machine.

`Malformed` and `Refused` now do the same thing, and that is the point: neither can be
fixed by resending, so both hold the queue and page a human. They stay separate outcomes
because they tag `edge.upload.failures` differently and send an operator to different
machines -- one to the contract, one to the credentials.

Caveat worth saying out loud: `TelemetryService` returns none of these statuses. It
answers OK and names its per-reading rejections in `rejected_message_ids`. A failed
Compose client-certificate check ends the TLS handshake before the service can return a
gRPC `Unauthenticated` status. `Malformed` and `Refused` therefore remain defensive
classifications for a proxy, application auth interceptor, or version skew that makes
the two sides disagree about the contract.

`ResourceExhausted` is deliberately left in the transient bucket. It is a rate limit as
often as it is "your message is too large", and backoff is the right answer to the first.

# Dropping versus stalling

Draining oldest-first makes the head of the queue a single point of failure. One record
the cloud will never take blocks every reading behind it. Four options:

**Stall forever.** Never loses a byte, and that is how it reads on a slide. In practice
the buffer keeps filling behind the stuck record until it hits `Buffer:MaxDepth`, at
which point the receiver starts shedding with 429 and the sensors' Polly retries give up.
"Never lose data" turns into "lose all the *new* data instead", which is worse, because
the new data is the data someone is watching a dashboard for.

**Drop the whole batch.** Throws away 199 readings the cloud never objected to, on the
evidence of one it did.

**Have the cloud name the offender (chosen).** `TelemetryResponse.rejected_message_ids`
carries the ids the server validated and refused. The gateway atomically copies exactly
those original rows to its bounded quarantine, adds their settled-ID markers, removes
them from the live queue, counts them on `edge.telemetry.rejected`, and keeps sending
full batches throughout.

**Rejected: isolate by resending at batch size 1.** A whole-call `InvalidArgument` says
only "something in there was bad", so finding out which meant re-sending the batch one
reading per round trip -- 200 round trips to drop one record, with the queue still
filling behind it. It also needed a cap on consecutive drops, because a cloud that called
*everything* invalid would empty the buffer one record per round trip and every drop
would look locally reasonable. Naming the ids removes the search and the cap with it.

The cost is that the server must now validate. `TelemetryReadingValidator` holds the
rules, and each one has to be a condition no retry can fix, because failing it deletes
the reading. Anything transient belongs in the catch around `ProduceAsync` instead, which
leaves the reading in the buffer.

# Two lists, not one count

`accepted_message_ids` and `rejected_message_ids` mean opposite things and are deleted for
opposite reasons. Accepted is durably Kafka's, so the gateway may forget it. Rejected will
fail identically forever, so the gateway *must* forget it or stall. Anything in neither
list is still the gateway's, and goes out on the next pass.

A single `accepted_count` could not say this. A prefix count only describes a server that
stops dead at its first failure -- the moment the server skips an invalid reading and
carries on, no one number can distinguish "the reading I refused" from "the reading I
never reached". The order of the batch stops being load-bearing at the same time, since
the response names readings rather than positions.

# This is permanent delivery loss, with bounded forensic evidence

A rejected reading can create a sequence gap inside an emulator run. `make verify` reports those
gaps using inferred restart boundaries; it does not use the invalid `MAX(sequence) - COUNT(*)`
calculation across all restarts. A database-only audit still cannot assign a gap to an intentional
sensor drop versus a downstream rejection, nor can it infer a dropped tail after the final row.
The reliable edge-side evidence for a cloud rejection is therefore the error log,
`edge.telemetry.rejected`, and the original payload in `QuarantinedTelemetryRecords`. Operators can
inspect the newest rows through `GET /buffer/quarantine?limit=100`; the endpoint caps each response
at 200 rows.

Quarantine is not another delivery queue. Ingestion has deterministically refused the reading, so
the uploader must not send it forever. Quarantine retention defaults to 168 hours and 10,000 rows;
age and count cleanup run in the same durable store that owns settlement. When either bound removes
a row, only its forensic copy disappears—the reading had already left the delivery contract when
the cloud explicitly rejected it.

# Not handled

- Quarantine has no export or operator acknowledgement workflow. Its time and count bounds protect
  disk space, which also means evidence expires unless an operator copies it elsewhere.
- Nothing rate-limits how much the cloud may reject. A validation rule someone tightens, or a proto
  skew that makes every reading look invalid, can empty the live buffer as fast as batches go out
  while the bounded quarantine evicts older evidence. `edge.telemetry.rejected` is therefore an
  alert, not merely a graph.
- `_consecutiveFailures` and `_consecutiveUnreachable` form an implicit state machine on a
  `BackgroundService`. They are deliberately not one field: `_consecutiveFailures` sizes
  the backoff and is bumped by refusals and local faults too, while
  `_consecutiveUnreachable` gates the one loud "cloud unreachable" line. Sharing a counter
  meant a refusal could silence the outage log.
- A reading with an empty `MessageId` cannot be named in `rejected_message_ids`, so the
  gateway would hold it forever. The receiver answering 400 to a reading without one is
  what keeps such a row out of the buffer; nothing downstream re-checks it.
