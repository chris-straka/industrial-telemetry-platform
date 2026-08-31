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
