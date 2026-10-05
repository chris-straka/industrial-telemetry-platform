# Learning IndustrialPlatform

A guided tour for someone who wants to understand, change, and defend this code in an interview.
The existing design notes in this folder go deeper on single topics; this page ties them together.

## 1. The problem

Factories, wind farms, and fleets of vehicles produce a steady stream of sensor readings from
devices with little memory and an unreliable uplink. The cloud side has to (a) not lose readings
once the site has accepted them, even through hours-long outages, (b) spot a machine going wrong
while there is still time to act, and (c) tell an operator what to do about it. Real products in
this space (AWS IoT Greengrass, Azure IoT Edge, Litmus, Samsara) are built from the same parts
this repo implements at small scale: an edge store-and-forward gateway, a durable log, streaming
detection, and an alerting path that tolerates duplicates.

## 2. Concepts you need

**Best-effort before 202, durable after.** The sensor never blocks on the network; it drops and
counts samples when its bounded channel fills. Custody starts only when the edge gateway has
fsynced a reading and returned `202`. See `AGENTS.md` "Reliability boundary" and
`src/Industrial.Sensor.EdgeGateway/Features/Buffer/ReceiveTelemetryEndpoint.cs:126`.

**SQLite WAL with `synchronous=FULL`.** WAL lets readers and the writer proceed concurrently;
`FULL` fsyncs the WAL on every commit, so a power cut after `202` cannot lose the row. The pragma is
per connection, so an EF interceptor sets it every time:
`src/Industrial.Sensor.EdgeGateway/Features/Buffer/SqlitePragmaInterceptor.cs:40`.

**Bounded buffer with backpressure.** Admission is an atomic reservation against `MaxDepth`
(`src/Industrial.Sensor.EdgeGateway/Features/Buffer/BufferDepth.cs:18`); when full, the gateway
answers `429` with `Retry-After` instead of growing without limit.

**Settle only what the cloud names.** The uploader deletes a buffered row only when ingestion lists
its `MessageId` as accepted or permanently rejected (`src/Protos/telemetry.proto:53`). Anything
unnamed, such as a Kafka hiccup, stays queued. See
`src/Industrial.Sensor.EdgeGateway/Features/Upload/UploaderWorker.cs:233`.

**At-least-once into an idempotent sink.** Kafka and the consumer may deliver twice. A unique
index on `MessageId` makes a replay a no-op, and the consumer commits an offset only after Postgres
succeeds (`src/Industrial.Diagnostics.Worker/Features/Diagnostics/TelemetryConsumerWorker.cs:157`).

**Transactional outbox.** The anomaly row and its pending alert commit together
(`TelemetryConsumerWorker.cs:329`); a separate publisher claims rows with `FOR UPDATE SKIP LOCKED`
(`src/Industrial.Diagnostics.Worker/Features/Diagnostics/AlertOutboxPublisherWorker.cs:126`).

**Online IID spike detection.** `model.zip` is ML.NET's `DetectIidSpike` configuration, not a
trained model. Each machine gets its own engine whose p-value compares the new reading with that
machine's last 20 (`src/Industrial.Diagnostics.Worker/Features/Diagnostics/ML/ModelEngine.cs:90`).

**Range gating.** A p-value only means something relative to the history it was computed over.
The emulator's dropout fault reports -999 C; one of those in the window widens the reference so
much that a real 245 C overheat looks normal. Implausible readings are therefore flagged by a rule
and never enter a detector window (`ModelEngine.cs:83`, and the warm-up query filter in
`TelemetryConsumerWorker.cs`).

**Robust statistics for diagnosis.** The offline advisor uses median and MAD rather than mean and
standard deviation, so earlier faults barely move the baseline
(`src/Industrial.Diagnostics.Worker/Features/Diagnostics/Advice/RuleBasedDiagnosisAdvisor.cs:48`).

**LLM as optional enrichment.** Diagnosis runs after the decision has committed, so a slow or
failing model can delay an alert's text but cannot change whether the alert exists. Gemini is
opt-in, its prompt is built from the same computed evidence
(`src/Industrial.Diagnostics.Worker/Features/Diagnostics/Advice/GeminiDiagnosisAdvisor.cs:39`),
and failures fall back to the offline text (`AlertOutboxPublisherWorker.cs:210`).

**Fan-out without a backplane.** Each Web API replica uses its own Kafka group so every replica
sees the full stream for its local SignalR clients (`src/Industrial.Web.Api/Program.cs:209`). The
dashboard dedupes by `MessageId` because delivery is at-least-once
(`src/Industrial.Web.Dashboard/src/app/lib/dedupe.ts:4`).

## 3. Reading order

1. `src/Industrial.Shared/TelemetryContracts.cs`: the two Kafka envelopes.
2. `src/Protos/telemetry.proto`: the gRPC batch contract and its frozen field numbers.
3. `src/Industrial.Sensor.Emulator/Program.cs`: acquisition vs transmission loops and the fault
   model (every 7th reading 245 C, every 10th -999 C).
4. `src/Industrial.Sensor.EdgeGateway/Features/Buffer/`: admission, SQLite, quarantine.
5. `src/Industrial.Sensor.EdgeGateway/Features/Upload/UploaderWorker.cs`: batching and settlement.
6. `src/Industrial.Ingestion.Api/Features/TelemetryService.cs`: per-reading validation and Kafka
   produce.
7. `src/Industrial.Diagnostics.Worker/Features/Diagnostics/ML/ModelEngine.cs`: detector state.
8. `src/Industrial.Diagnostics.Worker/Features/Diagnostics/TelemetryConsumerWorker.cs`: the
   commit/rewind loop, dedupe, and the outbox write.
9. `src/Industrial.Diagnostics.Worker/Features/Diagnostics/Advice/`: evidence, analysis, advisors.
10. `src/Industrial.Diagnostics.Worker/Features/Diagnostics/AlertOutboxPublisherWorker.cs`.
11. `src/Industrial.Web.Api/Program.cs` and `src/Industrial.Web.Dashboard/src/app/`.
12. `tests/Industrial.Diagnostics.Tests/DetectorQualityTests.cs`: how the detector numbers in the
    README are produced.

## 4. Exercises

1. **Run the detector replay.**
   `dotnet test tests/Industrial.Diagnostics.Tests -c Release --filter DetectorQualityTests --logger "console;verbosity=detailed"`.
   Predict the overheat recall before you look.
   <details><summary>Answer</summary>About 703/704 overheats and 544/544 dropouts with no false
   positives on normal readings.</details>

2. **Remove the range gate.** In `ModelEngine.InspectAsync`, comment out the `!IsPlausible` early
   return and rerun exercise 1. What happens to overheat recall, and why?
   <details><summary>Answer</summary>It drops to 0/704. Every 10th reading is -999, so the 20-reading
   p-value window nearly always contains one or two. Against a reference spanning -999..110, 245 is
   not unusual. Dropouts are still caught because they are the most extreme value.</details>

3. **Make the detector stricter.** Change `confidence: 90.0` in `src/Industrial.Data.ML/Program.cs`
   to 99, run `dotnet run --project src/Industrial.Data.ML` to rewrite `model.zip`, and rerun the
   replay. Predict the effect on recall and false positives.
   <details><summary>Answer</summary>Higher confidence demands a smaller p-value before flagging,
   so false positives cannot rise and recall can only hold or fall; the replay tells you which.
   The artifact's SHA-256 also changes, which is why every row persists `DetectorVersion`:
   old decisions stay attributable to the detector that made them. Restore the file with
   `git checkout src/Industrial.Diagnostics.Worker/model.zip` afterwards.</details>

4. **Read an offline diagnosis.** Write a test that calls `RuleBasedDiagnosisAdvisor.Diagnose` for
   245 C with oil pressure 31 and a normal history. Which cause does it name?
   <details><summary>Answer</summary>"lubrication loss: overheating together with low oil
   pressure". Oil below 35 turns a sudden spike into the lubrication branch.</details>

5. **Trend vs spike.** Feed the advisor a history whose last five readings rise strictly, then a
   reading above the last. Why five?
   <details><summary>Answer</summary>The pattern becomes `RisingTrend`. For independent noise, five
   strictly rising values in a row happen 1 time in 5! = 120, so the rule rarely fires by
   chance.</details>

6. **Break settlement.** In ingestion, make a Kafka produce failure add the ID to
   `rejected_message_ids`. Which regression test fails, and what would happen in production?
   <details><summary>Answer</summary>In production the gateway would delete its durable copy of a
   reading that never reached Kafka: silent data loss. Run `dotnet test
   tests/Industrial.Ingestion.Tests` to see whether a test catches it; if none does, that is
   the test to write.</details>

7. **Outage drill (needs Docker).** `make upd`, then `make chaos-cloud-down`, wait a minute,
   `make chaos-cloud-up`, then `make verify`. Watch `edge_queue_depth` in Grafana.
   <details><summary>Answer</summary>Depth climbs during the outage and drains oldest-first after.
   `verify` should report no duplicate IDs; gaps are attributed to sensor-side drops, not the
   pipeline.</details>

## 5. Interview questions

The bullet-by-bullet question list is in [Interview.md](Interview.md); short answers follow.

1. **Why is the detector not a trained model?** IID spike detection compares each value with a
   rolling reference window; there are no learned parameters. Calling `training_data.csv` training
   data would overclaim. A learned model would need labelled faults, evaluation, and a registry
   (see [ML/MLOps.md](ML/MLOps.md)).
2. **How did you find the dropout bug?** By replaying the emulator's exact fault cadence through
   the production artifact and counting recall per fault type. Overheat recall was 0/704. The
   detector's output was "correct" for its window; the window was polluted.
3. **Why median/MAD in the advisor?** The history contains earlier faults. Mean and standard
   deviation are dragged by one 245 or -999; the median and MAD are not.
4. **What if Gemini is slow or down?** The anomaly row and its outbox row are already committed.
   The publisher gives the advisor 10 s, then writes the offline diagnosis prefixed with
   "AI unavailable" and increments a failure metric. The alert is still published.
5. **How do you avoid the LLM making things up?** It receives computed evidence (robust z, p-value,
   oil pressure, recent temperatures, the rule-based pattern) and is told to use only that and to
   call out a sensor fault. The offline advisor gives a deterministic answer to compare against.
6. **Effectively-once with Kafka?** At-least-once delivery into an idempotent sink: a unique
   `MessageId` index, offsets committed only after the write, and dashboard dedupe for alert
   duplicates from the outbox.
7. **Why does each Web API replica use its own consumer group?** SignalR connections are local to a
   replica. A shared group would split the stream so each browser saw only some events.
8. **What does the edge gateway do during a 30-minute outage?** It keeps accepting onto SQLite up to
   `MaxDepth`, sheds with `429` beyond that, and drains oldest-first afterwards, preserving
   `OccurredAt` so charts show when readings happened.

## 6. Connections

- [TxMonitoringPlatform](https://github.com/chris-straka/TxMonitoringPlatform) is the fintech fork
  of this pipeline: the edge fleet and LLM are gone, and the interesting parts are an explainable
  risk policy, Redis sliding-window velocity, integer minor units, and an end-to-end latency
  assertion. Read its `docs/LEARN.md` for those.
- `cdc-pipe` covers the outbox/inbox pattern on its own; `durable` is a workflow engine with the
  same "commit, then do the side effect" discipline.
- `tickstore` and `lsmstore` show the storage side: append-mostly logs like the SQLite WAL here.
- `ebpf-observe` and the OpenTelemetry setup here are two angles on the same observability
  problem.
