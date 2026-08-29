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

`UploadBatchAsync` returns `UploadResult(UploadOutcome, Accepted)` instead of throwing.

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
| `Answered` | none (normal return) | delete the first `Accepted`, retry the rest | 1 |
| `Unreachable` | `Unavailable`, `DeadlineExceeded`, `Internal`, everything unlisted | backoff, retry forever | 0 |
| `Malformed` | `InvalidArgument`, `OutOfRange` | narrow to one record, then drop that record | 1 |
| `Refused` | `Unauthenticated`, `PermissionDenied`, `Unimplemented` | log loudly, buffer forever, drop nothing | 1 |

The last two leave the gauge at 1 on purpose. The cloud answered; a rejection is proof of
reachability, and an operator paged for "cloud unreachable" would go looking at the wrong
machine.

Caveat worth saying out loud: `TelemetryService` today never returns any of these
statuses. It catches everything and returns OK with a short `accepted_count`. `Malformed`
and `Refused` come from a proxy, a future auth interceptor, or a server that starts
validating. The classification is defensive, and the honest version of that sentence is
"this arm is currently unreachable in this deployment".

`ResourceExhausted` is deliberately left in the transient bucket. It is a rate limit as
often as it is "your message is too large", and backoff is the right answer to the first.

# Dropping versus stalling

Draining oldest-first makes the head of the queue a single point of failure. One record
the cloud will never take blocks every reading behind it. Three options:

**Stall forever.** Never loses a byte, and that is how it reads on a slide. In practice
the buffer keeps filling behind the stuck record until it hits `Buffer:MaxDepth`, at
which point the receiver starts shedding with 429 and the sensors' Polly retries give up.
"Never lose data" turns into "lose all the *new* data instead", which is worse, because
the new data is the data someone is watching a dashboard for.

**Drop the whole batch.** Throws away 199 readings the cloud never objected to, on the
evidence of one it did.

**Isolate, then drop one (chosen).** A whole-call rejection sends the next pass at batch
size 1, which identifies the offending record in one extra round trip. Only that record
is dropped, and it is counted on `edge.telemetry.poisoned`.

# The cap, and why the number matters less than its existence

`MaxConsecutivePoisonDrops = 10`, reset by any accepted batch.

Without a cap, "isolate and drop" has a catastrophic failure mode: a cloud that returns
`InvalidArgument` for *everything* -- a bad deploy, a proto skew, a validation rule
someone tightened -- would empty the buffer one record per round trip, and every drop
would look locally reasonable. The cap exists because the two failure modes are
distinguishable by count. A genuinely bad reading is rare and isolated. A broken cloud
rejects the eleventh one too.

Above the cap the uploader deliberately switches to the failure mode rejected above: hold
everything, log, let the queue back up. That is the failure an operator can still fix.

# This drop is real loss, and `make verify` will say so

A dropped reading breaks `MAX(SequenceNumber) == COUNT(*)` for its device. That is not a
flaw in the check -- it is the check working. The invariant this repo protects is that
loss is impossible *silently*; a poison drop is loss that is counted, logged with its
`MessageId`, and visible as a gap. The alternative was a stall that eventually loses far
more, and reports nothing at all.

# Not handled

- No dead letter. A dropped reading is gone, not parked. A second SQLite table would keep
  it for inspection, at the cost of a table nothing drains and a policy for when it is
  emptied.
- The cap resets on any success, so a cloud alternating between accepting and rejecting
  could still drop steadily. Bounded by rate, not by total.
- `_isolating`, `_consecutivePoisonDrops`, `_consecutiveFailures` and
  `_consecutiveUnreachable` form an implicit state machine on a `BackgroundService`. It is
  the honest cost of dropping poison records at all. The last two are deliberately not one
  field: `_consecutiveFailures` sizes the backoff and is bumped by refusals and local
  faults too, while `_consecutiveUnreachable` gates the one loud "cloud unreachable" line.
  Sharing a counter meant a refusal could silence the outage log.
