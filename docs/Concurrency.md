# Atomics, cache lines, and the shared drop counter

Context: `AcquisitionWorker._dropped` in the emulator. One `long`, incremented by every
device task, read by a sampled log line. Small enough to explain completely, which makes
it a good place to hang everything below.

## `Interlocked` is not a lock

Three different things get called "locking" and only two of them are:

| | what it is | cost |
| --- | --- | --- |
| `Mutex` | a named, kernel-backed lock that works **across processes** | syscall, microseconds |
| `lock` / `Monitor` | in-process mutual exclusion, one thread owns it, others block | cheap uncontended, blocks when contended |
| `Interlocked` | **lock-free atomics** on a single variable. Nobody owns anything, nobody blocks | one CPU instruction, nanoseconds |

`Interlocked` is a different category, not a lighter mutex. It makes one read-modify-write
on one variable indivisible, and that is all it does.

Why it is needed: `_dropped++` compiles to three operations — read, add, write. Two tasks
can both read 40, both write 41, and one increment vanishes. There is no error, just a
count that drifts low forever.

Equivalents elsewhere:

```c
// C11
_Atomic long dropped;
atomic_fetch_add(&dropped, 1);          // or __atomic_fetch_add, the compiler builtin
```
```cpp
// C++11
std::atomic<long> dropped;
dropped.fetch_add(1, std::memory_order_relaxed);
```

## `lock xadd`, and where the name comes from

`Interlocked.Increment(ref x)` becomes roughly `lock xadd [x], rax` on x86-64 (`ldaddal`
on ARM64, which is what actually runs on Apple Silicon).

**xadd = exchange-and-add.** Two operands, a memory destination and a register source:

```
temp = dest          // dest is 40
dest = dest + src    // dest becomes 41
src  = temp          // the register comes back holding 40
```

The register carries the addend in and the destination's **old** value out. That is the
fetch-and-add convention, and it is why every caller gets a distinct number back — which
the emulator relies on: exactly one caller can see `dropped % 100 == 1`, so the sampled
warning needs no lock of its own. (`Interlocked.Increment` returns the *new* value; the
JIT just adds the increment back onto what the instruction returned.)

**`lock` is a separate concern.** It is an instruction prefix that makes the whole
read-modify-write indivisible — the core holds that cache line exclusively for the
duration so no other core can interleave. It has nothing to do with which value comes
back.

**The name** is inherited from Win32's `InterlockedIncrement`, which .NET copied. It is the
railway-signalling sense of the word: Victorian signal boxes had mechanical bars linking
the levers so a signalman physically could not pull a combination that routed two trains
onto the same track. Operations constrained against conflicting orderings — not threads
being locked together.

## The cache hierarchy, and why line size is uniform

Levels differ in **capacity and latency**. They do not differ in **line size** — the line
is the unit of transfer between all of them, so it has to match. On this machine
(`sysctl hw.cachelinesize`) it is **128 bytes**; x86-64 is 64.

Vocabulary, since the abbreviations are opaque:

- **P-core** = *performance* core, **E-core** = *efficiency* core. Apple Silicon is
  heterogeneous: a few fast power-hungry cores and more slow frugal ones, and the OS
  scheduler picks. `perflevel0` is the P cluster, `perflevel1` the E cluster.
- **L1d** = L1 **data** cache. Its sibling **L1i** holds **instructions**. L1 is split in
  two because a core fetches an instruction and reads data in the same cycle, and they
  have completely different access patterns. L2 onward is unified.

Measured on an M4 Mac mini (`sysctl hw.perflevel*`):

| | cores | L1d each | lines in L1d | L2 | shared by |
| --- | --- | --- | --- | --- | --- |
| P-cores (`perflevel0`) | 4 | 128 KB | 1024 | 16 MB | all 4 |
| E-cores (`perflevel1`) | 6 | 64 KB | 512 | 4 MB | all 6 |

So on a 10-core M4 there are **20 L1 caches** (an L1i and an L1d private to each of the
10 cores) and **2 L2 caches** (one per cluster, shared by the cores in it). There is no
conventional L3; Apple Silicon has a System Level Cache shared with the GPU and other
blocks, which `sysctl` does not report.

Line count is just capacity ÷ 128. The number that matters is not how many lines you have
but **how many distinct lines an operation touches**, and whether they were already
resident.

## MESI, and why a counter ping-pongs

MESI is the classic cache-coherence protocol, named for the four states a cache line can
be in within one core's cache:

- **M**odified — this core has changed it; memory is stale; no other core has a copy.
- **E**xclusive — this core has the only copy and it is clean.
- **S**hared — several cores hold clean copies.
- **I**nvalid — the copy here is dead, fetch it again before use.

State is tracked **per line, per cache**. A core's L1d holding 1024 lines has 1024
independent states; the line carrying `_dropped` can be Modified while its neighbour is
Shared and a third is Invalid.

The rule that drives everything: **a core can only write a line it holds in M or E.** To
write, it must invalidate every other core's copy and take ownership.

These states are **mutually exclusive across cores**. If one core holds a line Modified,
no other core has a valid copy at all — "Shared here, Modified there" cannot happen. That
is the invariant the protocol exists to maintain.

The handshake when core B wants to write a line it currently holds Shared:

1. B issues a *read-for-ownership* on the interconnect: "I intend to write this line."
2. Every other core holding it marks its copy **Invalid**. A core holding it **Modified**
   must first write back (or forward) the current data, since memory is stale.
3. B now holds it **Exclusive**, transitions to **Modified**, and writes.

Every step is coherence traffic that did not exist when only one core touched the line.

So when device tasks on different cores increment `_dropped`:

1. Core A takes the line Modified, increments.
2. Core B wants to increment. It must invalidate A's copy and pull the line over.
3. Core A wants it back. The line migrates again.

The line bounces between L1s. The increment itself is a couple of nanoseconds; the
coherence traffic is the cost. (Real ARM implementations use MOESI or MESIF variants that
add states to avoid some write-backs, but the ownership rule is the same.)

Note this is *true* sharing — every task really is incrementing the same variable. The
contention is inherent, not accidental.

## False sharing

**False** sharing is when two threads **on different cores** touch *different* variables
that happen to sit on the same cache line. Different cores is the whole premise — two
threads on one core share that core's L1, so there is no coherence traffic to generate. There is no logical sharing at all, but coherence operates on lines,
not variables, so the hardware makes them fight anyway:

```csharp
private long _dropped;   // task A hammers this
private long _acquired;  // task B hammers this -- 8 bytes away, same line
```

Two unrelated counters, one line, full ping-pong. The classic version is an array of
per-thread counters, `long[] counts` — 16 of them fit in one 128-byte line, so "give each
thread its own counter" accidentally makes things *worse* than one shared counter.

On a 128-byte line that captures twice as many neighbouring variables as a 64-byte one,
so the same code false-shares more readily on Apple Silicon than on x86.

## Padding, and registers

The fix is to give each hot counter its own line — pad to 128 bytes here. Yes, that means
120 wasted bytes to protect 8. That is the trade: memory is cheap, coherence traffic is
not. There is no API for "reserve me a cache line" — you cannot address lines, only control
*layout* so that a variable lands alone on one:

```csharp
[StructLayout(LayoutKind.Explicit, Size = 128)]
struct PaddedCounter
{
    [FieldOffset(0)]
    public long Value;   // 120 bytes of nothing follow it
}
```

`Size = 128` guarantees the struct **occupies** 128 bytes. It does not guarantee it starts
on a 128-byte boundary, so it can still straddle two lines and defeat the point. Real
isolation needs aligned allocation (`NativeMemory.AlignedAlloc`) and unsafe code. This is
why padding is a last resort rather than a default.

On registers: ARM64 general-purpose registers (`x0`–`x30`) are **64 bits**, so a `long`
fits in exactly one. There are also 32 SIMD/vector registers of 128 bits each. Registers
sit above L1 and are the fastest storage a core has.

But **caches are coherent and registers are not** — that is precisely what MESI buys you,
and it stops at the cache. A value living only in a register is invisible to every other
core, so anything other threads must observe has to reach memory, and memory moves in
cache-line units. The variable is 8 bytes; the hardware moves 128.

## Why `_dropped` is shared and not per-device

**Kept: one counter for all devices.** Drops are a property of the *channel*, and the
channel is shared. When it fills, every device drops. Per-device counters would show the
same saturation divided N ways, which tells you nothing extra.

**Rejected: one padded counter per device, summed on read.** This is the right shape when
the counter is genuinely hot: each task increments a line nobody else touches, and the
totals are added up only when something reads them — a log line or a metric scrape. Cost
moves from the constant path to the rare one. Not done because at `DeviceCount=4` and
2 readings/sec the contention is unmeasurable, and it would trade a two-line drop handler
for a padded array plus a summing read. Revisit at ~1000 devices on a saturated channel,
which is the only regime where the ping-pong is real.

**Kept: `long`, not anything wider.** (*Widening* means using a larger integer type for
range or atomicity. *Padding* means adding unused bytes around a variable for cache-line
isolation. Different problems — the first is about the value, the second about its
neighbours.) In C# `long` is `System.Int64` — exactly 64 bits on
every platform and runtime, guaranteed by the spec. The LP64/LLP64 ambiguity that forces
C to reach for `long long` does not exist here. `Interlocked.Increment(ref long)` is also
atomic on 32-bit platforms, which a plain `long++` would not be.

## See also

- `sensor.telemetry.dropped` in `Infrastructure/SensorMetrics.cs` — the graphable version
  of this counter, and the only place sensor-side loss is ever visible.
- `docs/Observability.md` for the OTel side.
