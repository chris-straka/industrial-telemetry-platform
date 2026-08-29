# OTel

Span: A single unit of work (e.g., "Save to Postgres"). It has a start time, end time, and tags.
Trace: A collection of Spans that represent one full journey (Emulator, API, Kafka, Worker).
Tracer: The object in your code that creates Spans.
Meter: The object in your code that creates Metrics (like a speedometer or an odometer).

Log: discrete event "temp hit 100°C" (What happened?)
Metric: Aggregate number over time "avg temp over 10 minutes" (How is the system performing?)
Trace: story of a single req "how long did this take?" (Where is the bottleneck?)

An **instrument** is the thing you actually record measurements with -- "instrument" in the
measuring-instrument sense, nothing to do with UI. A Meter is the factory that creates
them, and it is identified by name so the exporter can subscribe: `AddMeter("Industrial.Sensor.Emulator")`.

The four that matter, and how to pick:

| instrument | shape | use when | example here |
| --- | --- | --- | --- |
| `Counter<T>` | only ever goes up | you are counting events that happened | `sensor.telemetry.dropped` |
| `UpDownCounter<T>` | up and down, you push each delta | a level you can track incrementally | -- |
| `ObservableGauge<T>` | a callback OTel invokes at scrape time | a level you can read on demand | `sensor.channel.depth` |
| `Histogram<T>` | distribution, bucketed | you care about spread, not just the total | request durations |

Counter vs Gauge is the one people get wrong. A Counter is a monotonic total -- "how many
have we ever dropped?" -- and you ask Prometheus for `rate()` or `increase()` to see it per
second. A Gauge is a value right now -- "how deep is the channel?" -- which is meaningless
to rate() and meaningful to graph directly.

Observable (callback) vs regular (push) is a separate axis: use Observable when the value
already exists somewhere and you can just read it (`channel.Reader.Count`), and a pushed
instrument when the value only exists at the moment something happens (a reading was
dropped).

A gauge is a SAMPLE, and everything between two samples is invisible. The reader wakes on
its interval (60s by default), calls the callback once, and that single number is the only
thing Prometheus ever stores for that window. If the edge buffer climbs from 5 to 900 and
drains back to 12 inside one interval, the graph shows 5 then 12 and the outage leaves no
trace at all. A Counter does not have this problem, because every `Add` is accumulated and
the total still carries what happened between samples.

That is the argument for a short export interval during the store-and-forward demo, and it
is also why loss is proven with `SequenceNumber` gaps in Postgres rather than by watching a
gauge. Metrics show you the shape of an outage; they are not evidence of what was lost.

`Counter<long>.Add(1)` returns **void**. That matters more than it sounds: an instrument
can never drive control flow, so anything that needs a value back (like the emulator's
"log every 100th drop" sampling) still needs its own `Interlocked.Increment` alongside the
counter. See `docs/Concurrency.md`.

These types live in `System.Diagnostics.Metrics` in the BCL, not in the OTel packages --
OTel just subscribes to them. That is why instrumenting a library does not force OTel on
its consumers.

You use OTel collector to send those things to prometheus, Loki, and Tempo.

Then you visualize in Grafana.

# Loki

Saves all your logs.

# Prometheus

Metrics DB.

A target is what prometheus is scraping from (http://localhost:8889/metrics).
A rule is logic that Prometheus evaluates against stored data ("If CPU > 80% for 5m, fire alert").

The official OTel metric for API response time is http.server.request.duration.
Prometheus automatically converts the dots to underscores.

# Spans Across Processes

A span never crosses a process boundary. When the gateway uploads to the cloud, the cloud
does not continue the gateway's span -- it starts its own, as a child. Disposing an
`Activity` ends that one span and stamps its duration; it says nothing about the trace,
which is still open as long as someone downstream is working.

What actually travels is the trace context, carried in the W3C `traceparent` header:
trace id, parent span id, and sampling flags. The gRPC and HttpClient instrumentation
inject it on the way out and read it on the way in, which is why both spans land under one
trace in Tempo without either side passing an id by hand.

In .NET the OTel vocabulary is spelled differently, because `System.Diagnostics.Activity`
predates OpenTelemetry and Microsoft mapped the existing type onto the spec rather than
adding a second one.

| .NET | OTel |
| --- | --- |
| `ActivitySource` | Tracer |
| `Activity` | Span |
| `ActivityContext` | SpanContext |
| `ActivityLink` | Link |
| `ActivityKind` | SpanKind |
| `SetTag` | attribute |

Two places in this repo break the automatic propagation, both deliberately. The gateway
buffers readings in SQLite, so the sensor's traceparent is stored on the row and replayed
later as a LINK rather than a parent -- see `UploaderWorker.BuildTraceLinks`, and note that
one upload fans in many sensor traces while a span may only have one parent. Kafka has the
same problem and has not been solved yet (`TODO.md`).

# Tempo

Popularity: Jaeger was the king for years.
Tempo is currently the fastest-growing because it integrates perfectly with logs (Loki) and metrics (Prometheus) inside Grafana.

# Terminology

