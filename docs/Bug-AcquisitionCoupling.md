# Bug: acquisition was coupled to transmission (fixed)

Found while reviewing the emulator. Worth keeping because the same bug existed at two
different layers of this system, which is the interesting part.

# The original shape

```cs
while (await timer.WaitForNextTickAsync(stoppingToken))
{
    var reading = Generate();
    await client.PostAsJsonAsync(route, reading, ct);  // <-- the whole loop waits here
}
```

One loop, doing two jobs, sequentially.

# Why it's wrong

`PeriodicTimer` signals ticks. It does NOT queue them.
If you are not sitting on `WaitForNextTickAsync` when a tick fires, that tick is gone.

So while the `await` on the POST is pending, ticks are being missed:

```
interval = 2s, Polly spends 10s retrying a slow gateway

t=0s   tick -> generate #1 -> send... (retrying)
t=2s   tick -> MISSED (we are inside the await)
t=4s   tick -> MISSED
t=6s   tick -> MISSED
t=8s   tick -> MISSED
t=10s  send finally returns
t=12s  tick -> generate #2
```

Four readings were not delayed. They were **never taken**.

That is the important distinction. Nothing queued up and drained late. The sensor's
sample rate silently dropped because the network was slow. A temperature spike during
those 10 seconds simply never existed as far as this system is concerned, and no metric
anywhere would show a gap, because the sequence numbers stay contiguous.

It also directly contradicted the comment sitting three lines below it:

```cs
// A real sensor doesn't stop sampling because a cable is out
```

It did stop sampling.

# The coupling, named precisely

Not "the Polly coupling" -- Polly only made it visible by making the send slow.

The coupling is **acquisition awaiting transmission**. Generating a reading and shipping
a reading are different jobs with different failure modes, and they were sharing one
thread of control, so the slower one throttled the faster one.

This is the same bug that motivated the Edge Gateway. The original emulator awaited a POST
to the CLOUD, so a cloud outage stopped data generation. Splitting the gateway out fixed
the blast radius (the emulator now only waits on a machine on the same LAN) but not the
shape -- acquisition was still awaiting transmission, just against a nearer target.

Fixing the blast radius is not the same as fixing the coupling.

# The fix

Two `BackgroundService`s that share nothing but a buffer:

```
AcquisitionWorker            Channel<TelemetryDto>          TransmissionWorker
  one task per device   ──►   bounded, in memory     ──►     drains and POSTs
  never touches network                                      never blocks acquisition
```

Same structure as the gateway one layer up:

```
receiver  ──►  SQLite (durable)  ──►  uploader
```

The difference between the two layers is what the buffer is made of, and that difference
is the whole architecture:

| | buffer | survives process death | if the buffer fills |
| --- | --- | --- | --- |
| sensor | RAM (`Channel`) | no | readings are lost at the sensor |
| gateway | disk (SQLite, WAL, fsync) | yes | sheds with 429 + Retry-After |

The sensor is allowed to lose data because it is a device with finite RAM, and it says so
out loud (`TryWrite` returns false, we count it, we log it). The gateway is not allowed to,
which is why it pays for an fsync per reading.

# What to say about this

The general claim: **any component that both produces and ships has this bug latent in
it.** The question to ask of any such loop is "if shipping takes 100x longer than
expected, does production slow down?" If yes, they are coupled, and the fix is always the
same shape -- a buffer between them, sized and durable according to what you can afford to
lose.
