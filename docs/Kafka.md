# Why use Kafka here?

Kafka prevents us from dropping events when an API is overloaded (API -> Kafka -> Worker -> DB).

Without Kafka, the DB could be overwhelmed with incoming sensor data.
Scaling workers only scales compute, which causes you to hit the DB bottleneck sooner (~5000 writes/second).
If DB is overwhelmed, the worker slows down, API waits longer for worker, API runs out of threads, events get dropped.
If you add Kafka, the API no longer waits for the worker but for Kafka to respond back quickly.

# Terms

A kafka broker is the node that stores events/msgs.

```cs
// Adds msgs to the broker, typically only instantiated once (thread safe)
IProducer kafka_producer = ProducerBuilder<MsgKey, MsgValue>(config).Build();

// Consumes msgs, has TCP connection to Kafka
// Kafka automatically divides the work across groupIDs
// Each consumer will read from a different partition in a topic
IConsumer kafka_consumer = new ConsumerBuilder<Ignore, string>(config).Build();
```

Topics are channels for related msgs, they have a 7 day storage limit by default.
Topic storage is only limited to the broker's disk space (drops older msgs when full)

A partition is a slice of a topic (they enable parallel reads).
A consumer group shares the same topic, each partition belongs to a single consumer.

An "offset" is the sequential ID number for a msg in a partition.
It's how Kafka tracks exactly where a consumer left off at.

A msg can store 1 MB by default but it can be configured.
It is not meant to hold heavy files (like images), just small, fast events.

# Queue vs Pub/Sub

You can give consumers the same consumer groupID or different groupIDs.

> Same Group ID (Load Balancing)

- Behavior: Kafka acts like a Queue. Partitions get divided among consumers.
- Characteristics:
    - Scale: You process data faster because work is shared.
    - Fault Tolerance: If one instance dies, Kafka automatically reassigns its partitions to the others.
    - Constraint: You cannot have more consumers than partitions; extra consumers will sit idle.

> Different Group IDs (Broadcasting/Fan-out)

- Behavior: Kafka acts like Publish/Subscribe. Every group gets a full copy of every message.
- Characteristics:
    - Independence: One group can be at the "beginning" of the topic while another is at the "end".
    - Isolation: If the "Analytics" group crashes, the "Alerts" group keeps working perfectly.
    - Cost: Higher resource usage on the Kafka Broker (more network and disk I/O).
- When:
    - If you wanted more than one microservice to have the API's telemetry data, you'd use Pub/Sub.

# Msg Ordering

By default, Kafka only orders msgs within a single partition, not across partitions.
If you don't specify a key for the msg, kafka will do round robin with msgs across partitions.
If you specify a msg key, then it will put all the msgs with the same key in the same partition.

tractorID = 1 -> partition 1
tractorID = 2 -> partition 2

# Producer Acknowledgments

The producer can fire and forget (0), wait for the partition leader (1) or wait for all brokers (All)

```cs
var producerConfig = new ProducerConfig
{
    BootstrapServers = "kafka:9092",
    // durability vs latency
    Acks = Acks.All, // Acks.None (0), Acks.Leader (1), Acks.All
    // optional: make it explicit
    EnableIdempotence = true
};
```

# Consumer Rebalancing

When an instance joins or leaves a group, Kafka stops all consumers in that group to re-assign partitions.
If one worker keeps crashing and restarting, it can constantly trigger these pauses and stop all work.

# ALO "At-Least-Once" Delivery

Kafka guarantees ALO delivery
A worker might receive the same msg twice if the broker crashes before committing the offset.
Your DB logic must therefore be idempotent, checking to see if the record already exists before inserting.

# Topic Creation

To control the defaults for a topic, you should eventually create them yourself.

```cs
using Confluent.Kafka;
using Confluent.Kafka.Admin;

var admin = new AdminClientBuilder(
    new AdminClientConfig { BootstrapServers = "kafka:9092" }).Build();

await admin.CreateTopicsAsync(new[] {
    new TopicSpecification {
        Name = "tractor-state",
        NumPartitions = 6,
        ReplicationFactor = 3,
        Configs = new Dictionary<string,string> {
            // by default, kafka will delete msgs after X time/size
            { "cleanup.policy", "delete" },
            // this will delete older msgs after a msg with the same key has a new value
            { "cleanup.policy", "compact" },
            { "delete.retention.ms", "86400000" }
        }
    }
});
```

# Parallelism vs Thread Safety

Because a consumer instance is not thread-safe, you cannot share one instance across multiple threads.
To read from 5 partitions using 5 threads, you must create 5 separate consumer instances.

# Healthcheck for Dev

Kafka goes through "fencing" and "leader election" stages.
A container can be "started" but refuse connections for 30 seconds while this happens.
This command ensures the broker is fully "unfenced" and ready for your API to send data.

```sh
healthcheck:
    test:
        [
            "CMD-SHELL",
            "kafka-topics.sh --bootstrap-server localhost:9092 --list",
        ]
    interval: 10s
    timeout: 10s
    retries: 10
```

Health checks provide automated recovery; observability provides human analysis.

Docker's healthcheck is a liveness and a readiness probe.
Readiness probe (blocking dependencies until ready)
Liveness probe (restarting the container if it fails repeatedly).

# Poison Messages

A poison message is one that fails every time it is processed. Nobody creates one on
purpose -- it is what you get when bad data meets a retry-on-failure loop, and the danger
is head-of-line blocking: the consumer retries the same message forever and everything
behind it stops moving.

Three ways to handle one, in rough order of how much work they are.

**Dead letter queue.** Move the message to a separate topic where unprocessable messages
land with the failure reason attached, then carry on. Nothing is lost and a human can go
look. This is the right answer for anything you are audited on -- see `docs/Fintech.md`,
where dropping a payment message is not an option. It is also the most machinery: another
topic, another consumer, and a decision about who reads it.

**Retry N times, then drop.** Give each message a few attempts on the theory that some
failures are transient, and give up after a counter runs out. This only earns its keep
when failures really are intermittent. A record that cannot be serialized will fail
identically on every attempt, so the retries are pure latency.

**Drop and log.** Delete it, log it loudly, and count it on a metric so the loss is
visible rather than silent.

The gateway does the third (`UploaderWorker`, counted as `edge.telemetry.poisoned`) and so
does the consumer (`TelemetryConsumerWorker`). That is a deliberate scope decision, not an
oversight: both are tracked in `TODO.md` as needing a DLQ. The reasoning is that a reading
which cannot be turned into a protobuf message is a bug in this codebase, not a data
problem a human could triage from a dead letter topic -- but that argument gets weaker the
moment the payload schema is owned by someone else.

Worth keeping straight: "poison pill" also means a sentinel value deliberately pushed onto
a queue to tell a consumer to shut down. Same words, opposite intent.
