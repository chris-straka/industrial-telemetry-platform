# Where the network hops are

```
sensor  --HTTP/JSON-->  edge gateway  --gRPC/HTTP2-->  ingestion api  --Kafka-->  worker
        (LAN, cheap)                  (WAN, flaky)                    (binary/TCP)
```

Each hop picks its own protocol because each hop has a different problem.

Sensor -> gateway is on the same LAN with many dumb clients, so it stays dead simple.
Gateway -> cloud crosses the internet with one client and a continuous stream, so it's gRPC.
Inside the cloud it's Kafka, because there is more than one consumer.

# HttpClient and sockets

A request does NOT create a socket.

`SocketsHttpHandler` keeps a connection pool and reuses an idle connection.
It only dials a new socket when every pooled connection is already busy.
`new HttpClient()` doesn't open anything either. The connection is dialled lazily on first use.

So fd exhaustion is a CONCURRENCY problem, not a per-request cost.
It happens when many requests are in flight at once against a hung server, each holding a connection open.
The per-process fd limit on Linux is 1024 or 4096 by default (`ulimit -n`).

The real sin is `new HttpClient()` per call and disposing it.
Disposing tears down that pool, and the sockets sit in TIME_WAIT for ~60s waiting for delayed packets.
`AddHttpClient` fixes this by pooling the handler for you.

> Typed clients are transient

`AddHttpClient<T>` registers T as TRANSIENT.

A `BackgroundService` is a SINGLETON.
Injecting the typed client straight into the worker captures a transient inside a singleton (a "captive dependency").
That pins one handler open for the life of the process and defeats handler rotation (which exists to pick up DNS changes).

Fix: keep the sender and the worker separate, resolve the sender from a scope per use.

# Thundering Herd

If the gateway restarts, 5,000 sensors throw at once.
Without jitter they all wait the same 2s and hammer it at the same millisecond, knocking it over again.

Polly fixes this with:

1. Exponential backoff (wait longer each failure: 2s, 4s, 8s)
2. Jitter (one sensor waits 2.1s, another 2.7s)

> Who owns the problem depends on how dumb the sensor is

A truly dumb sensor (4-20mA, Modbus RTU, HART) has no network stack.
The gateway POLLS it. It can't stampede because it never initiates anything.

A mid-tier IIoT sensor (ESP32 running MQTT/HTTP) does initiate, and its firmware has backoff built in.
My emulator is a .NET process, so it's in this category and Polly there is honest.

If clients genuinely can't back off, the fix moves to the SERVER:
bounded queue + return 429 with a RANDOMIZED `Retry-After`, so the server dictates the spread.

The architecture itself is the bigger mitigation.
The flaky internet hop now has ONE client (the gateway) instead of 5,000.
I didn't solve the stampede, I deleted the conditions for it.

# Circuit Breaker

Stops A from crashing when B is down.

1. Closed (normal): traffic flows freely
2. Open (tripped): failures cross a threshold (e.g. 5 in a row), reject all new reqs immediately
3. Half-Open (testing): after a cooldown (e.g. 30s), let one test request through

Without it, A keeps piling up requests against a dead B until A's own connection pool and fds max out.
That's a cascading failure.

# Retrying POST is only safe if the payload says so

The .NET standard resilience handler retries up to 3 times, and it retries POST by default.
Microsoft warns this can duplicate writes.

That's true if the write is identified by WHEN it arrived.
It's false if the write carries its own identity.

Every reading mints a `MessageId` at the SENSOR.
The gateway has a unique index on it, so a retried POST collapses into the row that already exists.

Retry safety is not a property of the verb. It's a property of the payload.

Same idea as Stripe's `Idempotency-Key` header (publicly documented, server-to-server, the cardholder never sees it).

# Head-of-line blocking

> HTTP/1.1

A connection must return responses in request order.
Send A (slow) and B (fast) and B is stuck behind A.

My emulator experiences NONE of this, because it only has one request in flight at a time.
There is never a second request queued behind the first.
.NET also opens several parallel connections per host, so even concurrent requests mostly dodge it.

The real HTTP/1.1 cost is one in-flight request per connection, not HOL as such.

> HTTP/2

Fixes HTTP HOL with multiplexed streams over one connection.
Does NOT fix TCP HOL.
If a packet drops, TCP must recover it before delivering any later bytes, which stalls every stream.
Streams are an HTTP concept; TCP doesn't know they exist.

> HTTP/3

Gives streams independent delivery at the transport layer, so one lost packet only stalls its own stream.

It runs on QUIC, which runs on UDP.
QUIC rebuilds reliability, ordering, congestion control and TLS 1.3 in USERSPACE.
Why not just fix TCP? Because TCP lives in the kernel and middleboxes across the internet have ossified it.
You can't deploy a new TCP. UDP is the escape hatch.

# Why gRPC on the gateway -> cloud hop

On the emulator's old direct-to-cloud POST loop, HTTP/2 bought almost nothing.
One request in flight means no multiplexing to do.

On the gateway hop it earns its keep:

1. JSON is text-heavy and needs repetitive string parsing to extract (CPU)
2. That link carries a continuous stream of batched telemetry, i.e. genuinely concurrent traffic on one connection
3. `telemetry.proto` is a shared schema both sides generate from, so a renamed field is a compile error

Plaintext gRPC needs its own port pinned to Http2.
There's no TLS between containers, so ALPN has nothing to negotiate with and the protocol has to be declared.
In prod you terminate TLS at the ingress (Istio) and this split collapses back to one port.

gRPC does NOT give you exactly-once.
If a stream dies after the server committed but before the ACK arrives, you still don't know.
Persistent queue + IDs + acknowledgement + dedupe is what solves it.

# Unary vs streaming is a call shape, not a transport

Every gRPC call is already an HTTP/2 stream.
Unary is not "the non-streaming one": it is a stream on which exactly one message is allowed in each direction.
The bytes are framed identically either way -- length-prefixed protobuf messages -- so the four shapes are only rules about HOW MANY messages each side may put on the stream.

| shape | client sends | server sends |
| --- | --- | --- |
| unary | 1 | 1 |
| server streaming | 1 | N |
| client streaming | N | 1 |
| bidirectional | N | N |

Nothing below the gRPC layer changes when you pick a different one.
Same connection, same protocol, same framing.
Calling that a change of transport is wrong and it hides where the real trade is.

`UploadTelemetry` is unary, carrying a `repeated TelemetryReading` field.

**Rejected: client streaming**, one message per reading on one open stream.
It is NOT slower in round trips -- the client fires all 200 messages without waiting for a reply between them, so both shapes are one round trip.
What it buys is the removal of the 4 MB per-message ceiling, because the limit applies per message and each message would hold one reading.
What it costs is an async enumerable on both ends instead of a method that takes a list.

That ceiling is the only thing streaming would relieve here, and this batch never approaches it.
`Uploader:BatchSize` is 200 and validates to at most 10,000; the ceiling sits near 50,000.
Paying real code complexity to lift a limit four times above the configurable maximum is not a trade.

> Streaming IS right for sensor data. Just not on this hop.

A device holding a connection open and pushing readings the instant it measures them is the textbook streaming case, and bidirectional with a per-reading ACK would be the better shape for it.

The uploader does not do that.
It wakes up, selects 200 rows, sends them, deletes what settled, and sleeps.
The readings already happened and are already on disk -- there is no live flow to hold a connection open for, only a pile of rows at rest to move.

Sensor -> gateway is a live feed. Gateway -> cloud is a batch drain. The call shape follows the job.

# Why a batch and not one reading per call

Batching is sized by outage recovery, not by steady state.
At 200 a batch, draining a 100,000-reading buffer is 500 round trips.
One reading per call is 100,000, and the gateway is still taking new readings while it drains, so a slow drain is a buffer that never catches up.

**Rejected: unary-per-reading.**
It makes every response trivially per-record, which is the one thing a batch has to work for.
The cost is a round trip per reading on the hop that has the most to move at exactly the worst time.

Batch size is a straight trade.
Larger means fewer round trips and more readings re-sent when a response never arrives, since an ambiguous failure re-sends everything in flight.
The ceiling is not tuning, though: one batch is one gRPC message under a 4 MB `MaxReceiveMessageSize`, which puts ~84 bytes a reading near 50,000.

Batching costs nothing in correctness now that `TelemetryResponse` names readings by `MessageId` rather than counting a prefix.
The cloud can accept, reject, or stay silent about any reading in the batch independently.

# Protocol layering

```
HTTP/1.1   text framing     -> TCP
HTTP/2     binary framing   -> TCP
HTTP/3     binary framing   -> QUIC -> UDP
gRPC       HTTP/2 + protobuf
Kafka      binary framing   -> TCP   (not HTTP at all)
```

Kafka is its own binary protocol over raw TCP.
Not request/response over resources: it's numbered API calls (Produce, Fetch, Metadata) with versioned schemas.
Clients hold long-lived connections and track partition assignments.
Confluent.Kafka wraps librdkafka, which speaks this natively.
(Confluent sells a REST Proxy that puts HTTP in front of a cluster, but that's an add-on, not the client.)

# Buffer durability

> The durability of a buffer should match the cost of losing one item in it.

| layer | buffer | survives process death | when full |
| --- | --- | --- | --- |
| sensor | RAM, `Channel<T>` | no | drops the reading, counts it, logs it |
| gateway | disk, SQLite WAL, fsync per commit | yes | sheds with 429 + Retry-After |

The sensor is allowed to lose data because it is a device with finite RAM and a lost
temperature sample is cheap. The gateway is not, so it pays for an fsync per reading.

Sizing follows from the arithmetic, not from taste. `Emulator:BufferCapacity` is 10,000
readings; 4 devices at one reading every 2 seconds is 2 readings/second; 10,000 / 2 =
5,000 seconds, so the sensor absorbs about 83 minutes of gateway outage before it drops
anything. Change the device count or the interval and that number moves.

Bounded is what prevents an OOM kill. An unbounded channel would grow until the process
is killed during a long outage, losing everything already buffered rather than only the
newest reading.

See Fintech.md for why this table inverts when the item is a payment.

# The durability handoff

> A component may delete its copy only once something that survives a restart holds it.

This is the rule the whole store-and-forward path is built on, and it is why `UploadTelemetry`
answers when it does.

`TelemetryResponse` is not a receipt. It is a TRANSFER OF CUSTODY.
The gateway deletes the SQLite row for every id the cloud names, so an id in `accepted_message_ids`
is a claim that the reading is now somebody else's problem.
Claiming that before it is true loses the reading permanently: the gateway has already forgotten it,
and the only other copy was a local variable in a process that just died.

The chain is a sequence of custody transfers, and each hop releases only after the next one commits.

| holder | how it survives | releases when |
| --- | --- | --- |
| sensor | nothing -- RAM | the gateway returns 202 |
| gateway | SQLite WAL, `synchronous=FULL` | the cloud names the id in a response |
| Kafka | replicated partition log | retention expires, long after the worker has consumed |
| Postgres | the system of record | never |

Note what the ingestion API is NOT in that table.
It has no store of its own -- no disk, no queue, nothing that outlives the method call.
So it cannot take custody, and the only thing it can honestly do is hold the gateway's call open until
Kafka has taken custody instead. That is exactly what this line does:

```csharp
await kafkaProducer.ProduceAsync(...);
accepted.Add(reading.MessageId);
```

`ProduceAsync` does not complete when the message is sent. It completes when the broker acknowledges it.
The `await` IS the handoff, and recording the id afterwards is what keeps `accepted` a list of durable
writes rather than a list of attempts.

That makes `Acks` load-bearing. At `acks=0` the task would complete on send and the whole guarantee
collapses into a lie. librdkafka defaults it to all, so this currently holds by inheritance rather than
by decision -- which is precisely the situation the "no fallback defaults" rule in CLAUDE.md exists to
prevent. Set it explicitly.

> Why 202 is the wrong verb here

`202 Accepted` means "I have taken responsibility for doing this later, no promises."
That is a fine answer to a caller that keeps its own copy, which is why the gateway's receiver returns it
to the sensor -- the gateway really has committed to disk by then, and the sensor was never going to
retain anything anyway.

It is the wrong answer to a caller that DELETES on it.
A gateway acting on 202 would be discarding data on a promise instead of on a fact.
`UploadTelemetry` is semantically a 200: this happened.

**Rejected: giving the ingestion API a durable buffer of its own**, so it could accept while Kafka is down.
That is store-and-forward one layer up, and it would work.
It loses because the buffer that absorbs a Kafka outage already exists one hop upstream, on a device
that survives power loss: Kafka down means `ProduceAsync` throws, fewer ids come back, and the gateway
simply keeps those rows. Adding another durable log in front of a durable log insures against the outage
of the one component whose entire job is being a durable log.
See TODO.md for why this is also not the "dual write" problem it was first filed as.

> Durability comes before batching, never after

Batching trades latency for throughput: reading 1 waits for reading 200 before anything is sent.
That trade is only acceptable while reading 1 is already safe.
Buffer in RAM and batch from there, and you have converted "one request per reading is expensive" into
"a crash loses 200 readings", which is a far worse problem than the one you set out to solve.

The gateway gets this order right: fsync to SQLite, ACK the sensor, and only then let the uploader batch
from disk. Reverse those two and the batching is what destroys the data.

# Transport vs payload

These are separate choices and it's easy to conflate them.

| hop | transport | payload |
| --- | --- | --- |
| sensor -> gateway | HTTP/1.1 | JSON |
| gateway -> cloud | HTTP/2 | protobuf |
| inside Kafka | Kafka binary/TCP | JSON string (TODO: protobuf) |

Kafka is a binary protocol currently carrying JSON TEXT in the message value.
Swapping that payload for protobuf doesn't change the transport at all.

The reason to swap it isn't parsing speed, it's schema enforcement.
`System.Text.Json` silently leaves unmatched properties at their defaults.
I already hit this: the API wrote `MessageId` into the JSON, the worker's DTO didn't have the field, and it became null forever. No exception, no log.

Cheap 90% fix: put the contract in a shared project both sides reference, so a rename is a compile error.
The gRPC hop already works this way. `telemetry.proto` IS the shared schema.
Schema Registry is the full fix, but it's another container to operate. Add it when there are producers I don't own.
