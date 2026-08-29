# Virtual Memory

My university notes adapted to this project

## Vaddr, Paddr, and page tables

Every process gets its own Vaddr space.
That's the isolation mechanism — two processes can both hold Vaddr `0x400000` and never see each other's data.

- **Page** = fixed-size chunk of Vaddr space. **Frame** = same-size chunk of Paddr space.
- 4KB pages are standard, so the low 12 bits are the **page offset** (2^12 = 4096).
- `Vaddr = [VPN | page offset]`. The VPN selects a page-table mapping, which returns the PFN. `Paddr = [PFN | same page offset]`.
- **The page table never stores the page offset.** Those bits pass through translation unchanged.

The MMU does translation in hardware, on every memory reference.
The page table lives in DRAM. `CR3` points at the active page table root; `CR2` holds the faulting Vaddr.

### Page-table entry (PTE) bits

The mappings and their metadata live in **page-table entries (PTEs)**. A leaf PTE maps one virtual page to a physical frame: it stores the PFN plus status and permission bits. In a multi-level page table, a non-leaf PTE instead points to the next page-table level.

`XWRADP` in the abstract:

- **X W R** — execute, write, read permissions.
- **A** access bit — set on any reference. Used by replacement to approximate recency.
- **D** dirty bit — set on write. Clean pages can be dropped without a write-back.
- **P** present bit — 0 means not in DRAM, so touching it faults.

On x86 also: `U/S` (0 = supervisor only, 1 = user accessible), `PWT` (write-through), `PCD` (cache disable), `G` (global — don't flush from TLB on context switch).

Permission violation or an unmapped Vaddr → **SIGSEGV**.

### Why multi-level page tables

A flat page table has one PTE for every possible VPN, including the vast unused parts of a process's Vaddr space. That is unaffordable:

```
32-bit:  (2^32 / 2^12) * 4B  = 4 MB   per process
64-bit:  (2^64 / 2^12) * 8B  = 32 PB  per process
```

So the VPN is split into one page-table index per level. For a common four-level x86-64 layout with 4KB pages:

```
Vaddr
[ L4 index | L3 index | L2 index | L1 index | page offset ]
     9b         9b         9b         9b           12b
```

`CR3` finds the L4 table. The L4 index selects a PTE pointing to an L3 table; the next index selects an L3 PTE, and so on. The leaf PTE supplies the PFN. Lower-level tables are allocated only for Vaddr ranges that need mappings, so unused regions need no leaf PTEs or lower-level tables. Five-level x86-64 adds another 9-bit index.

The cost is dependent lookups: conceptually, a four-level TLB miss needs up to 4 PTE reads before the data access. Caches and page-walk caches may satisfy those reads without going to DRAM, but the dependency chain is still what the TLB exists to avoid.

**Inverted page tables** flip it: one table for the whole system, indexed by frame, holding `(PID, VPN)`. Saves space, but you can't index into it by VPN — you linear-search or hash. Hashed page tables are the practical version.

## TLB

Cache of recent Vaddr→Paddr translations, typically ~512 entries, usually itself set-associative.

- Hit → translation in ~1 cycle, no page table walk.
- Miss → walk the page table (hardware-walked on x86, software-trap on MIPS), install the entry, retry.
- Hit rates are high because of temporal and spatial locality: a 4KB page covers a lot of sequential access.

On context switch the TLB is stale — entries belong to the old address space. Two ways out:

1. **Flush it.** Simple, and every subsequent translation misses until it refills.
2. **Tag it** with an address space ID (ASID on ARM/MIPS, PCID on x86). Entries from different processes coexist, no flush needed. This is the trick Liedtke leaned on to make microkernel address-space switches cheap.

Pages marked **global** (kernel mappings, same in every address space) survive the flush.

## The full walk: Vaddr to data

Assume a load through a **virtually indexed, physically tagged (VIPT)** L1 cache that obeys the size constraint described below.

The CPU produces a Vaddr for every instruction fetch and explicit load/store. An instruction fetch uses the program counter (`PC`/`RIP`); a load or store computes an effective address from the instruction's operands, such as a base register + scaled index register + displacement. In a user process, these program-visible addresses are virtual.

For the VIPT lookup, split that Vaddr three ways:

```
[       VPN       | cache set index | line offset ]
                  \__________  __________/
                             \/
                         page offset
```

The cache set index and cache-line offset both come from the page-offset bits, so translation cannot change them.

1. **Select the cache set with the Vaddr's set-index bits immediately.** No waiting on translation; that set's tags are read out.
2. **In parallel, look up the TLB** with the VPN.
   - TLB hit → PFN in ~1 cycle.
   - TLB miss → walk the page table. If the PTE says not-present, that's a page fault, see below.
3. **Form the translated Paddr and compare its physical tag** against the tags read out of the selected cache set.
   - Match and valid → **cache hit**, select the way, apply the cache-line offset, deliver the word. Done in a couple of cycles.
   - No match → **cache miss**. Send the Paddr to the next level (L2, L3, then DRAM), fill the line, evict a victim by the replacement policy, write back if the victim was dirty.
4. Data lands in the register.

The whole point of VIPT is that step 1 and step 2 **overlap**. You don't pay translation latency before you can even start the cache lookup.

# Caches

## Structure

A cache line (64B on x86-64, 128B on Apple Silicon) is the unit of transfer at every level — that's why line size doesn't change between L1/L2/L3, only capacity and latency do.

For a set-associative cache, an address splits into `tag | set index | line offset`:

- **line offset** — which byte within the cache line.
- **set index** — which cache set.
- **tag** — stored alongside the line to confirm identity on lookup.

**Associativity** is how many lines live in a set:

- **Direct mapped** (1-way) — one possible location per address. Fast, no way-selection, but two hot addresses with the same set index evict each other forever (conflict misses).
- **N-way set associative** — N candidate lines per set. Needs N tag comparators and a replacement choice within the set.
- **Fully associative** — any line anywhere. No conflict misses at all, but you'd need a comparator per line. Only viable for tiny structures like TLBs.

Three kinds of miss, worth naming because the fixes differ: **compulsory** (first touch, fix with prefetching), **capacity** (working set exceeds the cache, fix with a smaller working set), **conflict** (bad set-index distribution, fix with associativity or page coloring).

Write policies: **write-through** (write to cache and memory) vs **write-back** (write to cache, mark dirty, push to memory on eviction). Write-back is the norm; it's why the dirty bit exists at both cache and page level.

## Indexing and tagging schemes

- **PIPT** — physically indexed, physically tagged. Correct and simple, but you must finish translation before you can even index. Serialized latency. Used for L2/L3, where you already have the Paddr.
- **VIVT** — virtually indexed, virtually tagged. Fastest, no TLB in the path at all. Two problems:
  - **Homonyms** — the same Vaddr means different data in different processes. Fixed with ASID tagging or a flush per context switch.
  - **Synonyms/aliases** — two Vaddrs map to the same Paddr (shared memory, `mmap` of the same file). Now the same data sits in two lines and a write to one doesn't invalidate the other. This is the killer, and it's why VIVT is rare.
- **VIPT** — index virtually, tag physically. Overlap cache indexing with TLB lookup, and the physical tag kills the homonym problem outright.

**The VIPT size constraint:** the cache set-index + line-offset bits have to fit inside the page offset. Otherwise the set index uses VPN bits that translation can change, and aliases come back:

```
max cache size = page_size * associativity
4KB page * 8-way = 32KB
```

That is exactly why L1d is so often **32 KB, 8-way**. It's not a coincidence, it's the largest VIPT cache you can build on 4KB pages without alias handling. Want a bigger L1? Raise associativity, use bigger pages, or add hardware to detect aliases.

## Page coloring

When a cache is physically indexed and larger than `page_size × associativity`, some cache set-index bits come from the **frame number**, not the page offset. Those bits are the page's **color**.

Which frame the OS hands you therefore decides which cache sets your page can occupy. Allocate frames carelessly and a process's hot pages all land on the same colors, and you get conflict misses in a cache that had plenty of free space elsewhere.

**Page coloring** = the OS picks frames so a process's pages spread evenly across colors.

- Keeps conflict misses down without changing the hardware.
- Makes performance _repeatable_ — without it, the same binary on the same input varies run to run purely on which frames it happened to get.
- Second use: on VIPT caches, force Vaddr and Paddr to agree on the cache set-index bits, so aliases can't happen and you get VIPT above the normal size limit.
- Cost: it constrains the allocator. Under memory pressure the right color may not be free, and you either wait or take a worse frame.

Modern general-purpose kernels mostly gave up on strict coloring (physical memory is too fragmented, and it fights the buddy allocator). It survives where latency variance is unacceptable — real-time, some hypervisors, cache-partitioning schemes like Intel CAT.

### Coherence: update vs invalidate

With private per-core caches, the same line can sit in several caches at once. Coherence keeps them from diverging.

- **WI "Write Invalidate"** — writer invalidates every other copy, then writes. Subsequent readers miss and refetch. Repeated writes by the same core are free after the first (nobody else has a copy to invalidate), so it **amortizes well under write-heavy access**.
- **WU "Write Update"** — writer pushes the new value to every other copy. Readers never miss. **Wastes bandwidth** when nobody else actually reads it.

Real hardware is basically all WI, because writes cluster and most updated values are never read by anyone else.

**MESI** is the standard WI protocol: a line in each cache is Modified, Exclusive, Shared, or Invalid, and a core can only write a line it holds M or E. I have this written up properly in `docs/Concurrency.md` with the read-for-ownership handshake and the false-sharing consequences, so I'm not repeating it here.

Two things worth carrying forward:

- **Atomics are expensive on SMP, not because of the instruction but because of the coherence traffic.** `test_and_set` writes even when the value doesn't change, so it invalidates every other copy every time.
- **NCC** (non-cache-coherent) machines exist — GPUs, clusters, disaggregated memory. There, coherence is software's problem.

> > `docs/Concurrency.md` already covers why `Interlocked.Increment` on a shared `_dropped` counter ping-pongs a line between cores, and why padding to 128B fixes it. That's this section, applied.

## Segmentation vs paging

|                       | Segmentation                                | Paging                            |
| --------------------- | ------------------------------------------- | --------------------------------- |
| Unit                  | Variable-size, semantic (code, heap, stack) | Fixed-size, arbitrary             |
| Address               | `selector + offset`                         | `VPN + offset`                    |
| Fragmentation         | External (holes between segments)           | Internal (slack in the last page) |
| Visible to programmer | Yes, it matches program structure           | No, it's transparent              |

Segmentation matches how a program is _organised_; paging matches how memory is _managed_. Paging won because fixed-size blocks make allocation and swapping trivial — any frame fits any page.

x86-32 did both: Vaddr → segmentation unit → linear address → paging unit → Paddr. x86-64 mostly flattened segmentation away (base 0, limit max), keeping it only for `FS`/`GS` which is how thread-local storage is implemented.

## Page fault path

Assume this is the only runnable process

1. CPU issues a Vaddr. MMU walks the page table (after TLB miss) and finds `present = 0`.

2. MMU raises a **page fault exception**. Faulting Vaddr goes in `CR2`.

3. Trap to kernel: mode switch, switch to kernel stack, jump to the fault handler. The faulting instruction's state is saved so it can be _restarted_, not resumed after — this instruction never completed.

4. Handler classifies the fault:

- **Not in the process's address space** → SIGSEGV, done.
- **Permission violation** (write to a read-only page) → either SIGSEGV, or a legitimate **CoW** fault, see below.
- **Valid but not resident** → real page fault, continue.

5. Obtain the physical frame that will back the page. A file-backed page may already have a resident frame in the page cache. Otherwise take a free frame; if the free list is below its watermark, run replacement, and write back a dirty victim before reusing its frame.

6. Determine how to populate the frame:
   - A never-written anonymous page can be zero-filled; it has nothing to read.
   - A swapped-out page is read from its swap slot.
   - A file-backed page comes from the corresponding file offset, possibly from an already-resident page-cache entry.

   If the contents are not already resident, submit a storage I/O request.

7. If I/O is outstanding, block the faulting thread. Under the assumption that nothing else is runnable, the CPU idles; normally the scheduler runs another thread. That's why a major page fault blocks rather than spins.

8. Once the frame is ready — immediately for zero-fill/a page-cache hit, or after I/O completion wakes the blocked thread — the fault path updates the PTE: install PFN, set `present = 1`, set permissions. The device's **interrupt** reports completion; its handler does not directly edit the faulting process's PTE.

9. Return from the handler. **Restart the faulting instruction.** This time the walk succeeds, the TLB gets the entry, and execution continues.

**CoW** is the same machinery used deliberately: on `fork()`, parent and child share frames marked read-only. The first write traps, the handler copies the page to a fresh frame, marks it writable, and restarts. You only pay for pages actually modified.

## Working set, thrashing, and friends

- **Working set** — the set of pages a process actively references in a time window. Not its total allocation; the part it's actually touching. If the working set fits in RAM, page faults are rare.
- **Thrashing** — the sum of all working sets exceeds physical memory. Every process constantly evicts pages another process is about to need. The system spends its time paging instead of computing and throughput collapses. The fix is to reduce the multiprogramming level (suspend a process entirely) — adding more paging effort makes it worse.
- **Paging daemon** — kernel thread (`kswapd`) that keeps the free-frame list above a watermark by evicting in the background. The point is to have frames _already free_ when a fault happens, so the fault path doesn't have to synchronously write back a dirty victim. It runs when memory pressure is high and CPU is idle.
- **Swapper** — classically, moves an _entire_ process out to disk (medium-term scheduling), the blunt response to thrashing. Modern Linux swaps at page granularity, so "swapper" survives mostly as the name of PID 0.
- **Loader** — takes an executable, sets up the address space (maps text/data/bss/stack), usually via `mmap` so pages are demand-paged rather than read eagerly, then jumps to the entry point.
- **Linker** — resolves symbol references between object files and libraries. **Static** at build time (`.a`, symbols baked in). **Dynamic** at load/run time (`ld.so`, `.so`/`.dll`, one copy of libc shared across processes). Dynamic linking is why the same physical frames of libc back many processes at once.

Replacement in practice: true LRU is too expensive (you'd order every access), so kernels approximate it with the **access bit** — clock/second-chance sweeps the bit, giving pages that were touched since the last sweep another pass. Prefer evicting clean pages; never evict pinned pages (DMA buffers, kernel state).

**Pinning** a page keeps it resident no matter what. Required for DMA targets, because a device writing by Paddr has no idea the OS moved the page.

## Allocators

- **Kernel-level** — serves the kernel's own needs and process startup state, and tracks free memory.
  - **Buddy** — split power-of-two blocks until you find the smallest that fits. Coalesces with the neighbour ("buddy") on free, which controls external fragmentation. Internal fragmentation is bad for odd sizes — `task_struct` isn't a power of two.
  - **Slab** — caches of pre-formed objects of one common size, carved from contiguous slabs. Kills internal fragmentation for kernel objects and skips constructor work on reuse. Sits on top of buddy.
- **User-level** — the heap, `malloc`/`free`. After `malloc` returns, the kernel is not involved in that memory. `jemalloc`, `tcmalloc`, `dlmalloc` are alternatives tuned for different allocation patterns and thread counts.

**External fragmentation** = free memory exists but not contiguously.
**Internal fragmentation** = allocated block is bigger than requested.

## Page sizes and huge pages

4KB is standard. 2MB and 1GB huge pages exist.

Bigger pages mean **fewer PTEs, smaller page tables, and far better TLB reach** (one entry covers 2MB instead of 4KB), at the cost of **internal fragmentation** and coarser granularity for swapping and CoW.

Databases and JVM/CLR heaps often use huge pages, because a large contiguous heap with random access is exactly the case where TLB misses dominate.

> > Relevant if the worker's ML.NET inference ever becomes memory-bound — a model whose weights blow the TLB reach will spend real time in page walks. Measure before reaching for it.

# Concurrency

## Processes, threads, and what gets shared

- **Process** = program + address space + the state of all its threads.
- **Thread** = independent sequence of execution _inside_ a process's address space and resources.
- Threads share: code, heap, globals, file descriptors, the page table. Threads have their own: stack, registers, PC, thread-local storage.

**ULT vs KLT** — user-level threads are scheduled by a library the kernel can't see; kernel-level threads are scheduled by the kernel. The mapping matters:

- **1:1** — every ULT is a KLT. The kernel sees everything, so blocking one thread doesn't block the others, and traps are fast. Linux (NPTL), Windows, and effectively every modern system.
- **M:N** — many ULTs multiplexed onto fewer KLTs. Cheap creation and user-space context switches, but the kernel and the library are blind to each other, so a blocking syscall can stall unrelated threads. Solaris did this with LWPs. Linux tried it with NGPT and dropped it in 2003.

Go's goroutines and C#'s `async`/`await` are M:N-shaped ideas rebuilt above the OS rather than inside it. The scheduler is in the runtime, and the blindness problem comes back as "don't block the thread pool."

The split of process state exists because of this:

- **Light process state** — per-thread: registers, syscall args, signal mask, kernel stack.
- **Hard process state** — shared by all threads: the Vaddr→Paddr mappings.

Linux uses one `task_struct` (~1.7KB) per schedulable entity and doesn't distinguish process from thread structurally. `clone()` decides what's shared. The PID in a `task_struct` is really a task ID; `tgid` is the shared process-level ID.

## Context switch cost

A **context switch** is a CPU stopping one schedulable thread and starting another. The kernel saves enough of the outgoing thread's execution context — registers, program counter, stack pointer, and related state — to resume it later, then restores the incoming thread's saved context. Entering the kernel is only a **mode switch**; it is not a context switch if the same thread returns without the scheduler replacing it.

A switch is **voluntary** when the running thread blocks or yields, and **involuntary** when the scheduler preempts it.

**The steps:**

1. The running thread blocks/yields, or a timer interrupt gives the scheduler a chance to preempt it.
2. Save the outgoing thread's required execution state: general registers, PC, SP, flags, and FP/SIMD state as needed.
3. Scheduler picks the next thread.
4. If it's in a different _process_, switch the address space — on x86, load its page-table root through `CR3`.
5. Restore the incoming thread's registers and stack.
6. Resume the incoming thread at its saved PC, returning to user mode when appropriate.

**The costs, split:**

_Direct_ — the register saves/restores, the scheduler's own runtime, and an address-space switch when needed. Hundreds of cycles. Bounded and visible.

_Indirect_ — much bigger, and this is the actual answer to "what does L1/L2 have to do with context switches":

- **The caches go cold.** The incoming thread's working set isn't in L1/L2. Every early access misses and goes to L3 or DRAM, which is hundreds of cycles each. The new thread runs at a fraction of steady-state speed until its working set is resident again.
- **The TLB.** Without ASID/PCID tagging you flush it, so every early translation is a full page walk. With tagging you keep the entries but they still compete for capacity.
- **Branch predictor and prefetcher state** is trained on the old thread and mispredicts on the new one.

Liedtke measured the cache effect at ~864 cycles against ~100 cycles for the kernel crossing itself. **The indirect cost dominates by an order of magnitude,** which is why "just add more threads" stops helping, why cache affinity in the scheduler matters, and why event-driven servers can beat thread-per-request under load.

A switch between _threads of the same process_ is much cheaper — no `CR3` write, no TLB flush, and the caches likely still hold shared data.

## Synchronization

Why: any time two threads touch the same location and at least one writes, you need ordering, or you have a data race. When: around the shortest section that maintains the invariant. How:

| Primitive              | Use it for                                                                                      |
| ---------------------- | ----------------------------------------------------------------------------------------------- |
| **Mutex**              | One holder. The default.                                                                        |
| **Semaphore**          | Counting — N permits. Binary semaphore when you need someone _other than the owner_ to release. |
| **RW lock**            | Many readers or one writer. Worth it only when reads dominate; watch for writer starvation.     |
| **Condition variable** | Wait for a predicate, always inside a mutex, always in a `while` loop (spurious wakeups).       |
| **Monitor**            | Higher-level: lock + condvars bundled with the data. Java `synchronized`, C# `lock`.            |
| **Barrier**            | All N threads wait until the last arrives. Scientific/phased workloads.                         |
| **RCU**                | Lock-free reads, expensive writes, deferred reclamation. Kernel read-mostly structures.         |

**All of these need hardware atomics underneath.** You cannot build mutual exclusion out of plain loads and stores at reasonable cost — you need read-modify-write as one indivisible operation. The family is called fetch-and-Φ: `test_and_set`, `fetch_and_increment`, `compare_and_swap`, `fetch_and_store`.

### Spinlocks and contention

Spin instead of block when the critical section is **short** and contention is **low** — you save two context switches and keep the cache warm. Never spin on a single-CPU system, and never spin around anything that can block.

Three metrics, and they trade against each other:

- **Latency** — time to acquire a free lock. Wants an immediate atomic.
- **Delay** — time to notice a lock was just released.
- **Contention** — atomic memory traffic plus coherence traffic.

The progression, and what each fixes:

1. **Spin on `test_and_set`** — atomic every iteration, so every spin goes to memory and invalidates everyone. Terrible contention.
2. **Spin on read (test-and-test-and-set)** — spin on the _cached_ value, only issue the atomic when it looks free. Much less traffic, but on release everyone's copy is invalidated at once and they all storm the atomic together.
3. **Add backoff** — stagger the retries so they don't storm. Exponential backoff is the standard answer, and it's the same idea as jittered retry in a distributed system.
4. **Queue locks (Anderson's array, MCS linked-list)** — each waiter spins on its _own_ variable, and the releaser signals exactly one successor. Fair, minimal contention, O(N) space. MCS is the one that survived; it's what most modern kernel locks are built on.

> > Same shape as `docs/Networking.md`'s thundering herd and circuit breaker sections. Storm-on-release is storm-on-recovery. Jittered backoff is the fix at both scales.

### Deadlock and priority

Four conditions, all required: **mutual exclusion, hold-and-wait, no preemption, circular wait.** Break one. In practice you break circular wait by imposing a **global lock ordering**, or you break hold-and-wait with try-lock-and-back-off.

- **Priority inversion** — low-priority thread holds a lock a high-priority thread needs, and a medium-priority thread preempts the low one. The high-priority thread is now blocked behind a medium one. This is what killed Mars Pathfinder.
- **Priority inheritance** — the lock holder temporarily inherits the priority of the highest waiter, so it can finish and release.
- **Starvation** — a thread never gets scheduled. **Priority aging** raises the priority of long-waiting threads.

### Interrupts, signals, and handler deadlock

|         | Interrupts           | Signals              |
| ------- | -------------------- | -------------------- |
| Origin  | External hardware    | CPU or software      |
| Timing  | Async                | Sync or async        |
| Scope   | System-wide handlers | Per-process handlers |
| Masking | CPU interrupt mask   | Process signal mask  |

Traps are the synchronous kind — syscalls and faults.

**The classic deadlock:** a thread holds a mutex, a signal arrives on that thread, and the handler tries to take the same mutex. Two fixes: mask the signal for the duration of the critical section, or run the handler on a separate thread. SunOS's rule of thumb was **if the handler can block, give it its own thread; otherwise run it on the interrupted thread**, because thread creation is expensive.

**Top half / bottom half** is the general shape: the top half runs in interrupt context, does the minimum to acknowledge the device, and defers everything else to a bottom half that runs later with interrupts enabled. Keeping interrupt context short is what keeps latency bounded.

## Producer–consumer

The prereq project (digitizer → tracker → alarm, three threads, bounded buffers between them) is exactly the shape of my pipeline, so it's worth stating properly.

```
producer:                        consumer:
  lock(m)                          lock(m)
  while (buf is full)              while (buf is empty)
      wait(not_full, m)                wait(not_empty, m)
  enqueue(item)                    item = dequeue()
  signal(not_empty)                signal(not_full)
  unlock(m)                        unlock(m)
```

Three things that are always true and always forgotten:

1. **`while`, not `if`.** Spurious wakeups are real, and even without them another thread can win the lock between the signal and your wake-up.
2. **The buffer must be bounded.** An unbounded queue converts backpressure into an OOM kill. Blocking on a full buffer _is_ the backpressure signal.
3. **Two condition variables, not one.** One condvar means producers wake producers.

> > This is `Channel<T>` in the sensor emulator. Bounded capacity 10,000, and on full it drops-and-counts rather than blocking, because a device with finite RAM would rather lose a temperature reading than stall acquisition. `docs/Networking.md` has the sizing arithmetic. The choice of _which_ of the three full-buffer policies to take — block, drop-newest, drop-oldest — is the whole design decision, and it depends entirely on what one lost item costs.

## Concurrency vs parallelism

**Concurrency** is a structuring property: multiple logical flows in progress, interleaved. Possible on one core.
**Parallelism** is an execution property: multiple flows running at the same instant. Needs multiple cores.

Threads help even without parallelism, because they hide I/O latency — while one blocks on a read, another computes. That's concurrency doing useful work on one CPU.

Threads stop helping when: the workload is small (creation cost dominates), or CPU utilization is low while the context switch rate is high (you're paying switch cost for nothing).

## MP vs MT vs event-driven

The Flash/SPED web server study, compressed to what survived:

|                    | Pros                                      | Cons                                                 |
| ------------------ | ----------------------------------------- | ---------------------------------------------------- |
| **Multi-process**  | Simple, isolated                          | High memory, expensive switches, hard to share state |
| **Multi-threaded** | Shared address space, cheap switches      | Synchronization, needs kernel support                |
| **Event-driven**   | One flow of control, no sync, tiny memory | Blocks without async I/O                             |

The finding that matters: **the event-driven server won when everything fit in cache, and lost when it didn't** — because it blocked on synchronous disk I/O with no other thread to run. The hybrid (**AMPED**: event loop + helper processes/threads for the blocking operations only) won overall, because it had the small memory footprint of an event loop _and_ didn't stall on I/O.

That's still the design of every modern async runtime. `async`/`await` is an event loop; the thread pool is the helpers; and blocking inside an async method is exactly the failure mode the paper measured.

# Scheduling

I nearly cut this, then remembered k8s CPU limits are implemented as CFS quota, which makes it a production concern rather than a textbook one.

## Basics

- **Timeslice/quantum** — max uninterrupted time a task gets.
- **Preemptive** — the scheduler can take the CPU back on timer interrupt. **Run-to-completion** — it can't.
- **RR** — fixed timeslice, circular queue. Doesn't need to know runtimes and lands close to optimal average wait.
- **SJF** — shortest job first, minimizes average wait, but requires knowing runtimes (or estimating from history).
- **Priority** — pick from the highest non-empty queue. Risks starvation, needs aging.

The general trade: **larger timeslice** favours CPU-bound work (fewer switches, better throughput). **Smaller timeslice** favours I/O-bound work (requests launch sooner, better responsiveness). This is the same throughput-vs-latency tension as batching anywhere else.

**MLFQ** encodes it: assume a new task is interactive, give it a short slice; if it uses the whole slice without yielding, demote it to a longer slice at lower priority. Feedback without needing to know anything up front.

## CFS

Linux's default since 2.6.23, replacing the O(1) scheduler (which had jitter because a task had to wait for the whole active array to drain).

- Runqueue is a **red-black tree ordered by `vruntime`** — nanoseconds of CPU consumed, weighted by nice value.
- Always pick the leftmost node (lowest `vruntime`). Selection O(1), insertion O(log n).
- Nice value scales how fast `vruntime` advances: low-priority tasks accumulate it faster, so they're picked less.
- A new or waking task gets the _minimum_ `vruntime` in the queue, not 0 — otherwise it would monopolize the CPU catching up.

> > **This is where it becomes my problem.** A k8s CPU limit is not a core count, it's a CFS bandwidth quota: `cpu.max` gives the cgroup N microseconds of CPU per 100 ms period. Blow through the quota and every thread in the cgroup is **throttled** — descheduled until the next period boundary.
> >
> > The pathology: a container with `limits.cpu: 500m` and a runtime that spins up a thread pool sized to the _machine's_ core count can burn its whole quota in the first 20 ms of a period and then sit frozen for 80 ms. p99 latency goes through the roof while average CPU utilization looks low and harmless. The metric to watch is `container_cpu_cfs_throttled_seconds_total`, not CPU percentage.
> >
> > Mitigations: set CPU _requests_ without _limits_ for latency-sensitive services, size the .NET thread pool and `MaxDegreeOfParallelism` to the cgroup quota rather than `Environment.ProcessorCount`, or raise the limit. .NET _does_ honour the cgroup limit for `Environment.ProcessorCount`, but it **rounds up** — `500m` reports 1, `2500m` reports 3 — so the count is always at least as generous as the quota. `DOTNET_PROCESSOR_COUNT` overrides it outright and is the lever worth knowing.

## Multiprocessor scheduling

- **SMP** — all CPUs are peers with equal access to one memory. Uniform latency, shared and therefore contended path to memory.
- **NUMA** — memory is split into banks attached to sockets. Local access is fast, remote access crosses an interconnect and is slower. A NUMA-aware scheduler keeps a task on the node holding its memory.
- **Cache affinity** — keep a task on the CPU whose cache is already warm for it. Per-CPU runqueues plus a load balancer, rather than one global queue. This is the scheduler paying attention to the indirect context switch cost.
- **SMT/hyperthreading** — extra register sets per core so a second hardware thread can run while the first stalls on memory. It hides latency; it does not add execution units. Two compute-bound threads on one core contend and each gets roughly half. The win comes from **mixing** a memory-bound thread with a compute-bound one, which is exactly what Fedorova's CMT paper measured using CPI as the classifier.

> > In k8s this is why CPU requests are not interchangeable with cores: two pods scheduled onto sibling hyperthreads get nothing like two pods on separate physical cores. It's also why NUMA-aware placement is a real feature for latency-sensitive workloads.

# I/O and Storage

## Devices

Every device exposes control, status and data registers. The OS needs a driver per controller, behind a standard interface so the rest of the kernel doesn't care.

- **MMIO** — device registers are mapped into the Paddr space, so the CPU touches them with ordinary loads and stores. BARs (Base Address Registers) define the window.

- **Interrupts vs polling** — interrupts respond immediately but cost a handler entry and **cache pollution** (the handler evicts the running thread's working set). Polling costs CPU but batches nicely. High-rate NICs use both: interrupt on first packet, then poll (Linux NAPI), because at line rate the interrupt overhead alone would saturate a core.

- **PIO vs DMA** — PIO has the CPU move every byte; fine for a keyboard, hopeless for a NIC. DMA hands the transfer to a controller and interrupts on completion. Setup costs more than a store, so PIO still wins for tiny transfers. **The DMA target buffer must be pinned** — the device writes by Paddr and has no idea the OS moved the page.

**A block device** is any device addressed in fixed-size blocks with random access — disk, SSD, and the layer Linux exposes as `/dev/sdX`. It's the opposite of a character device (byte stream, sequential, no seek). Filesystems sit on top of the block layer and never talk to the driver directly:

```
process (files)
  -> VFS                 (uniform interface across filesystems)
  -> filesystem (ext4)   (which blocks hold this file)
  -> generic block layer (scheduling, merging, queueing)
  -> device driver       (this specific controller)
```

## Filesystem structures

- **inode** — one per file, holds the metadata (permissions, size, timestamps, owner) and the list of data blocks. **It does not hold the filename.** Blocks need not be contiguous. A fixed-size inode can only list so many blocks, so it uses **indirect pointers** — a block of pointers to blocks, then double- and triple-indirect. That's why small files are fast and huge files cost extra indirections.

- **dentry** — directory entry, one path component. Maps a name to an inode number. Lives in memory, cached aggressively, and is the reason path lookup isn't a disk walk every time.

- **superblock** — at the start of the partition, describes the filesystem's own layout: block size, where the inode table and bitmaps are, how much is free.

- **Bitmaps** — the block bitmap tracks free blocks, the inode bitmap tracks free inodes.

- **VFS** — the abstraction that lets one `read()` work against ext4, NFS, tmpfs and procfs alike.

**Hard link** = another dentry pointing at the same inode (same file, two names, inode refcount 2). **Soft link** = a file whose contents are a path.

Optimizations, all of which are just caching in disguise: the **buffer/page cache** (keep blocks in RAM), **I/O scheduling** (merge and reorder to reduce seeks), **prefetching** (spatial locality), and **journaling** (write intent to a log first, apply later).

## Log-structured storage

The idea I got the most out of, because Kafka _is_ one.

A normal filesystem updates data in place, which means seeks scattered across the disk and a read-modify-write for every small change. A **log-structured** store never updates in place — it **appends every change to a sequential log** and treats the log as the source of truth.

- Writes are sequential, which is the one access pattern every storage medium is good at.
- Small writes batch into one large segment write. **This is the fix to the small-write problem.**
- Reading requires reconstructing state from the log — expensive the first time, so you cache the result.
- Overwriting a block **invalidates the earlier copy in the log, leaving a hole**. Holes accumulate, so you need a **cleaner** that coalesces live blocks into fresh segments and frees the old ones. Log cleaning is the permanent tax on this design.

**Journaling** is the half-measure: write the intent to a log, apply it to the real structures, discard the log entry. You get crash consistency without giving up in-place updates.

> > **Kafka is a log-structured store with the abstraction left visible.** A partition is an append-only log split into segment files; an offset is a byte position in it; consumers are cursors, not queue-poppers, which is why two consumer groups can read the same data independently. Retention is the cleaner: time/size-based deletion drops whole segments, and **log compaction** is exactly the "coalesce live blocks, drop superseded ones" pass — it keeps the newest value per key and discards older ones.

> > The reason Kafka is fast isn't magic, it's that it does what the storage stack is best at: sequential appends, page cache instead of an application cache, and `sendfile()` to go from page cache to socket without copying into userspace. That last one is why "Kafka doesn't use much heap" — the OS is doing the caching.

> > The SQLite WAL in the gateway is the journaling version of the same idea: append the intent, `fsync`, apply later. `docs/Networking.md` covers why that hop pays for an fsync per reading and the sensor hop doesn't.

## Caching semantics and invalidation

From the distributed filesystem work (NFS, Sprite), which is really a set of answers to "when does a cached copy become wrong."

The options for propagating a write, weakest to strongest:

- **Eventual/periodic** — push on a timer. NFS: 3s for files, 30s for directories. Cheap, and clients are knowingly stale within a bound.
- **Session semantics** — read on open, write on close. Concurrent writers get last-close-wins, which is to say, data loss.
- **On every write** — write-through or invalidate immediately. Correct, expensive, and doesn't scale.
- **Leases** — the server grants a time-bounded right to cache. **A lease is a lock with an expiry, which means a dead client can't block anyone forever** — the lease just runs out. This is the mechanism that makes stateful caching survivable, and it's everywhere: NFSv4 delegations, DHCP, k8s leader election, ZooKeeper/etcd session TTLs.

Sprite's measurements are the reason for all of this: 33% of accesses were writes, 75% of files were open <0.5s, and 20–30% of data was deleted within 30 seconds of creation. **Most writes die before anyone else reads them**, so write-back with a delay and no propagation at all is usually right. When a file _is_ actively shared by a writer and a reader, Sprite disabled caching entirely for it and went to the server — accept a big penalty on the rare case to keep the common case fast.

> > That's the argument for cache-aside with a TTL over write-through in almost every service, and for invalidate-on-write over update-on-write. It's also why the memcached lesson below is about _ordering_ invalidations rather than propagating values.

# Networking

Only TCP and the pieces that keep showing up in backend work. Routing, BGP, SDN, packet classification and video streaming are in the original CN notes and aren't repeated here.

## The stack, and how data moves

Going down the stack on send, each layer **encapsulates** the layer above by prepending its header, so the PDU grows as it descends:

| Layer        | Adds                      | Job                                                                            |
| ------------ | ------------------------- | ------------------------------------------------------------------------------ |
| L7 app       | —                         | HTTP, gRPC, Kafka's binary protocol                                            |
| L4 transport | TCP/UDP header, **ports** | Demux to the right socket, plus reliability and flow/congestion control if TCP |
| L3 network   | IP header, addresses      | Host-to-host routing, **best effort**                                          |
| L2 link      | Frame header, MACs        | Hop-to-hop delivery on one link                                                |
| L1 physical  | —                         | Bits on the wire                                                               |

**Send path, concretely:** app calls `write()` on a socket → copy into the socket send buffer in kernel memory → TCP segments it according to `min(cwnd, rwnd)` and MSS, adds its header, starts the retransmit timer → IP adds addresses and routes → the driver builds a descriptor pointing at the buffer → **DMA** to the NIC → wire.

**Receive path:** NIC DMAs the frame into a ring buffer → raises an interrupt → the top half acknowledges it and schedules the bottom half → the bottom half walks the stack, strips headers, and uses the **4-tuple (src IP, src port, dst IP, dst port)** to find the socket → data lands in the socket receive buffer → a blocked `read()` wakes up and copies to userspace.

**That demux step is the answer to why ports exist.** An IP address gets you to a host; the NIC delivers one undifferentiated stream. The port is what lets the kernel decide that _this_ segment belongs to the API's listener and _that_ one to the Kafka client's connection.

Two copies in each direction (userspace↔kernel, kernel↔NIC) is the baseline cost. `sendfile()`, `mmap`, and io_uring exist to remove them.

## TCP

**Three-way handshake:**

```
1. SYN      SYN=1, seq=client_isn
2. SYN-ACK  SYN=1, seq=server_isn, ack=client_isn+1
3. ACK      SYN=0, seq=client_isn+1, ack=server_isn+1
```

Both sides exchange an initial sequence number. Teardown is four-way (`FIN`/`ACK` in each direction) because each direction closes independently.

**ARQ** is the reliability core: if no ACK arrives within a timeout derived from measured RTT, resend.

- **Stop-and-wait** — one packet in flight. Correct and unusably slow.
- **Sliding window** — N packets in flight without waiting. Both sides need buffers.

**Loss detection and recovery:**

- **Go-back-N** — ACK the highest in-order byte; on a gap, discard everything after it and resend from there. Simple, wasteful.
- **SACK** — the receiver reports exactly which ranges it holds, so the sender resends only what's missing.
- **Fast retransmit** — 3 duplicate ACKs for the same sequence number means later packets arrived but one didn't. Resend immediately rather than waiting for the timeout. Duplicate ACKs are the signal precisely because they prove the path is still delivering.

### Flow control — protecting the receiver

`rwnd` is advertised by the receiver: `rwnd = RcvBuffer - (LastByteRcvd - LastByteRead)`. The sender obeys `LastByteSent - LastByteAcked <= rwnd`.

The deadlock this creates: receiver advertises `rwnd = 0`, sender stops, receiver drains its buffer — but `rwnd` updates only ride on ACKs, and there's nothing to ACK. **Persist probes** fix it: the sender keeps poking with 1-byte segments so the receiver is forced to respond with a fresh window.

> > That is exactly the shape of a stalled consumer that never re-polls. Anything that only learns about capacity as a side effect of traffic needs a keepalive path for when traffic stops.

### Congestion control — protecting the network

`cwnd` is the sender's own estimate of what the _network_ can absorb. The sender is limited by `min(cwnd, rwnd)` — **flow control protects the receiver, congestion control protects everything in between.**

- **Slow start** — start at 1 MSS, add 1 MSS per ACK, so `cwnd` doubles every RTT. Exponential, despite the name, because it starts from the smallest possible window. Continue until the slow-start threshold.
- **AIMD** — the steady state. Additive increase: +1 MSS per RTT, a slow linear probe upward. Multiplicative decrease: halve on loss. `Increment = MSS × (MSS / cwnd)`.
- **Why AIMD and not AIAD or MIMD** — schemes that increase and decrease at the _same_ rate oscillate forever and never converge on a fair split. Differing rates converge. AIMD converges _and_ backs off fast enough to actually relieve congestion, which MIAD doesn't.
- **Loss is the congestion signal**, inferred from duplicate ACKs (mild — halve) or timeout (severe — back to 1). ECN is the explicit alternative, where routers mark instead of drop.
- **CUBIC** (the Linux default) replaces additive increase with a cubic function of the _time since the last congestion event_, growing fast when far from the previous ceiling and cautiously near it. Crucially it's **independent of RTT**, which fixes AIMD's unfairness toward long-RTT flows. Reno ramped far too slowly on high bandwidth-delay-product links.

**Throughput** falls out as `BW ≈ (MSS / RTT) × (1 / √p)` for loss probability `p`. The useful reading: **throughput is inversely proportional to RTT and to the square root of loss.** A little loss on a long link destroys throughput, which is the entire argument for terminating TLS close to users and for keeping chatty protocols off long paths.

**Fairness** is only approximate: shorter-RTT flows converge faster and get more, and one host opening N connections gets N shares. That second one is a deliberate strategy, not a bug — it's what browsers did before HTTP/2.

> > HTTP/1.1 vs 2 vs 3 and TCP head-of-line blocking are in `docs/Networking.md`. The connection to this section: **HTTP/2 multiplexing doesn't fix TCP HOL** because TCP guarantees in-order delivery of one byte stream, and it has no idea there are independent streams inside it. QUIC moves reliability up into userspace precisely so it can keep per-stream sequencing.

## Rate limiting and queueing

The router scheduling material, which is just backpressure with different vocabulary.

**Token bucket** — a bucket of `B` tokens refilled at `R` tokens/sec; sending consumes one. `B` sets the burst you tolerate, `R` sets the sustained rate. Two modes:

- **Shaping** — no tokens means the packet _waits_. Smooths bursts, adds latency, needs a queue.
- **Policing** — no tokens means the packet is _dropped_. No queue, no added latency, lossy.

**Leaky bucket** — output drains at a constant rate regardless of how bursty the input was. Strictly smooths; overflow is dropped.

**Why FIFO isn't enough:** one heavy flow fills the queue and every other flow's packets get tail-dropped. No isolation.

**Fair queueing** gives each flow its own queue and serves them so each gets its share. The ideal (bit-by-bit round robin) is unimplementable because packets are indivisible, and emulating it needs a priority queue keyed on computed finish times — O(log n) per packet.

**DRR "Deficit Round Robin"** is the practical version: each flow gets a **quantum** of bytes per turn, and whatever it couldn't use (because its next packet was too big) carries forward in a **deficit counter**. O(1) per visit, and it gives fair _byte_ throughput regardless of packet size. Plain round robin doesn't — a flow with big packets wins.

> > This is the vocabulary for the gateway's shedding behaviour. `429 + Retry-After` is **policing** (drop, tell the client the rate). A bounded `Channel<T>` that blocks the producer is **shaping**. Per-device quotas so one chatty sensor can't starve the others is **fair queueing** — and DRR is the right mental model because readings aren't uniform in size. Worth knowing that "just add a queue" converts a loss problem into a latency problem rather than solving it.

## Consistent hashing

Naive sharding is `server = hash(key) % N`. Lose or add a server and `N` changes, so **almost every key remaps** and the entire cache misses at once.

**Consistent hashing** puts both keys and servers on a ring of fixed size (say 2^32). A key belongs to the first server clockwise from its position.

```
key_position   = hash(key) % RING_SIZE
target_server  = first server clockwise from key_position
```

Now `N` is not in the lookup function. Adding or removing a node only remaps the keys in **one arc** — roughly `K/N` keys instead of all of them. **Virtual nodes** (each server placed at many ring positions) even out the arc sizes and spread a failed node's load across all survivors instead of dumping it on one neighbour.

> > **Kafka partitioning is this.** `tractorID → partition` is hashing a key onto a fixed set of slots so that all readings for one device land in one partition and stay ordered relative to each other. Two consequences I should be able to state:
> >
> > 1.  **Ordering is per-partition, so the key choice defines the ordering guarantee.** Keying by device gives per-device ordering, which is what anomaly detection needs; it gives no global ordering, which is fine because there's no global invariant.
> > 2.  **Kafka uses modulo, not a ring** — `hash(key) % num_partitions`. So _changing the partition count reshuffles every key_, and a device's history splits across two partitions with no ordering between them. That's why partition counts are treated as immutable in practice and why you over-provision partitions up front. It's the same failure consistent hashing was invented to avoid, and Kafka accepts it because partitions are cheap to over-allocate and rebalancing consumers is the more common operation.
> >
> > Hot partitions are the failure mode: one very chatty device pins one consumer at 100% while the rest idle. Same skew problem as an unbalanced hash ring.

## Cache invalidation at scale

The memcached-at-Facebook lessons, which generalize to any read-through cache.

**Look-aside cache:** client checks the cache, misses, reads the DB, then populates the cache. Writes go to the DB and **invalidate** the cache entry rather than updating it.

**The stale-set race:** A misses and reads value `v1` from the DB. B writes `v2` and invalidates. A then sets the cache to `v1`. **The cache now holds a value older than the DB, and nothing will ever correct it** — it isn't stale-with-a-TTL, it's permanently wrong until the next write. The fix is **leases**: the cache hands a token to whoever missed, and only a set carrying the current token is accepted.

**Ordering across replicas:** with multiple cache clusters, invalidations racing over the network arrive out of order and one cluster ends up with a different value than another. The fix is to stop having clients invalidate at all and instead **derive invalidations from the DB's commit log** — a single service tails the log and broadcasts in commit order. The log is the only thing that has a real ordering.

> > That's change-data-capture, and it's the argument for driving cache invalidation off Debezium/the WAL rather than from application code. It's the same insight as the Kafka log: **when you need a total order, get it from the thing that already has one.**

---

# Distributed Systems

## What makes it hard

A DSys is a set of nodes that communicate only by **msgs** over an interconnect but present as one coherent system. A multicore CPU is _not_ one — it uses shared memory, which makes it parallel, not distributed.

Three sources of non-determinism:

1. **Asynchrony** — msgs can arrive instantly, within a bound, or never. No bound is the realistic model.
2. **Failures** — failstop (dies and stays dead), transient, or **Byzantine** (misbehaves arbitrarily).
3. **Concurrency** — no global order of events.

The design constraint that follows: **computation per event must be large relative to communication cost**, or the msgs dominate and you'd have been better off on one machine. That's the whole argument against chatty microservices.

**Failure modes**, worth naming precisely because the recovery differs:

- **Fail-stop** — stops, and others find out. The model almost everything assumes.
- **Fail-silent/crash** — stops, nobody is told. You need timeouts to detect it, and **a timeout cannot distinguish "dead" from "slow"**. This one sentence is the source of most distributed systems pain.
- **Omission** — running but dropping some msgs.
- **Timing** — responds, but outside its window.
- **Byzantine** — arbitrary or malicious. Needs `n ≥ 3f + 1` to tolerate `f` of them, which is why it's confined to blockchains and avionics.

**Detection** is heartbeats, and heartbeats are only ever a guess.

> > k8s liveness/readiness probes and Kafka consumer group heartbeats are both this. `session.timeout.ms` is the point where the coordinator declares a consumer dead and rebalances — and if the consumer was merely doing a long GC or a slow ML.NET inference, you get a **spurious rebalance**: work is reassigned, the old consumer wakes up and commits an offset for a partition it no longer owns. That's the "slow vs dead" problem billing me directly. The fix is `max.poll.interval.ms` sized to the real worst-case processing time, and doing heavy work off the poll thread.

## The 8 fallacies

Every one of these is an assumption that silently holds in dev and breaks in prod: the network is reliable · latency is zero · bandwidth is infinite · the network is secure · topology doesn't change · there is one administrator · transport cost is zero · the network is homogeneous.

## Time and causality

You can't use wall-clock timestamps to order events across nodes, because clocks drift relative to real time and relative to each other. What a DSys actually needs is not time but **causality**.

**Happens-before (`->`)**: `a -> b` if they're consecutive events in one process, or `a` is a send and `b` is its receive. Transitive. If neither `a -> b` nor `b -> a`, they're **concurrent** (`a || b`), and no ordering between them is meaningful.

**Lamport (scalar) clocks** — a counter per process.

```
local event:      C = C + 1
send:             attach C
receive:          C = max(C_local, C_msg) + 1
```

Gives `a -> b ⟹ C(a) < C(b)`. **The converse does not hold** — `C(a) < C(b)` tells you nothing, they might be concurrent. Ties break on process ID to get a total order, and that total order is arbitrary but consistent, which is enough for things like distributed mutual exclusion.

**Vector clocks** — a vector of counters, one slot per process. Increment your own on an event, take the element-wise max on receive.

```
VT1 < VT2   iff  every element <=  and at least one <
VT1 || VT2  iff  neither is less than the other
```

Now `a -> b ⟺ VT(a) < VT(b)` in **both** directions, so you can finally _detect concurrency_ rather than just respect causality. The cost is O(N) per clock and per msg, which is why they don't scale to large N and why systems use them for conflict detection (Dynamo, Riak) rather than for ordering everything.

> > **A W3C trace context is a causality mechanism.** `traceparent` propagates a trace ID plus the parent span ID across every hop, so the collector can reconstruct the happens-before graph of a request across the emulator, gateway, API, Kafka and worker. The reason spans nest correctly without synchronized clocks is that **parentage is explicit rather than inferred from timestamps** — the same reason Lamport used counters instead of clocks.
> >
> > Two practical consequences: (1) Kafka **breaks** the trace unless you inject `traceparent` into message headers and extract on consume, because the producer and consumer are different processes with no call stack between them; (2) span _durations_ still come from local clocks, so a child span can appear to start before its parent on a skewed host. The causal graph is trustworthy, the timeline is not.

**Chandy-Lamport snapshots** record a consistent global state without stopping the system: an initiator records its own state and sends a **marker** on every outgoing channel; on first marker a process records its state and starts recording other incoming channels; on subsequent markers it stops recording that channel. Needs FIFO channels.

The subtle and useful part: **the recorded state may never have actually existed** at any instant. It's _consistent_ — no effect recorded without its cause — but it's a possible state, not a real one. That's fine for detecting **stable properties**, things that stay true once true: deadlock, termination, token loss. It's useless for anything transient.

> > Same caveat as reading a distributed trace or a set of Prometheus scrapes: you're looking at a consistent cut, not a photograph.

## Consistency models

Ordered strongest to weakest. Each step down buys availability and latency.

- **Strict** — every op visible everywhere instantly, ordered by real time. **Impossible in a DSys** (would require a global clock). Useful only as the reference point.
- **Linearizability** — single operations appear atomic at some point between their invocation and response, and that order respects real time for **non-overlapping** ops. Overlapping ops may be ordered either way. This is what "strong consistency" means in practice.
- **Sequential** — all nodes see the _same_ order, and each process's own ops keep their program order — but that global order need not match real time. Op A can be ordered after B even though A finished before B started.
- **Causal** — only causally related ops are ordered. Concurrent writes can be seen in different orders by different nodes.
- **Eventual** — with no new writes, replicas eventually converge. Says nothing about when, or what you read in the meantime.

**Serializability** is the transactional cousin: concurrent txs produce _some_ serial order. **Strict serializability** = serializability + real-time order, i.e. the transactional analogue of linearizability. They are not literally the same thing — linearizability is about single objects, strict serializability about multi-op transactions.

**CAP** — under a **partition** you choose availability or consistency. Stated as a conjecture by Brewer in 2000, **proven** by Gilbert and Lynch in 2002.

The common misreading is treating it as a permanent three-way choice. It isn't: **P is not optional** (networks partition whether you like it or not), so CAP only bites _during_ a partition. **PACELC** fixes the framing — if **P**artition, choose **A** or **C**; **E**lse, choose **L**atency or **C**onsistency. The else branch is the one that governs your system 99.9% of the time.

Cassandra/Dynamo chose P+A. Spanner/Megastore chose P+C.

## Delivery guarantees and idempotency

| Guarantee             | What it means                 | How you get it               |
| --------------------- | ----------------------------- | ---------------------------- |
| **AMO** at-most-once  | Never duplicated, may be lost | Send once, don't retry       |
| **ALO** at-least-once | Never lost, may be duplicated | Retry until acknowledged     |
| **EO** exactly-once   | Neither lost nor duplicated   | ALO + dedupe at the receiver |

**There is no exactly-once delivery.** The sender cannot distinguish "the msg was lost" from "the ACK was lost", so it must either risk losing it or risk sending it twice. What exists is **exactly-once _processing_**: at-least-once delivery plus an idempotent consumer or a dedupe table keyed on a message ID.

**Idempotency** is the property that applying an operation twice has the same effect as once. `SET balance = 100` is idempotent; `balance -= 10` is not. When the operation isn't naturally idempotent you make it so with an **idempotency key** the receiver records before acting.

> > This is the single most directly applicable thing in the whole DC course. My pipeline is ALO end to end: the emulator retries on failure, the gateway retries the cloud POST, Kafka redelivers on rebalance or offset-commit failure. So **every consumer has to be idempotent or dedupe**, and the natural key is `(deviceId, readingTimestamp)` or an explicit `MessageId`.
> >
> > The specific trap: committing the Kafka offset **before** the DB write means a crash in between loses the reading (at-most-once). Committing **after** means a crash in between redelivers it (at-least-once). There is no ordering of those two writes that gives exactly-once — you need the dedupe. `docs/Networking.md` says the same thing about gRPC: a stream dying after commit but before ACK is unresolvable at the transport layer.
> >
> > Kafka's "exactly-once semantics" is real but narrower than the name: it's idempotent producers (sequence numbers per partition dedupe retries at the broker) plus transactions spanning a consume-process-produce cycle. It does not extend to an external DB unless that write is in the same transaction, which for Postgres it isn't.

## Consensus

**Consensus** = getting distributed processes to agree on a value. Three properties:

- **Agreement** — all correct nodes decide the same value.
- **Validity** — the decided value was proposed by someone.
- **Termination/liveness** — a decision is eventually reached.

Safety = agreement + validity. Correctness = safety + liveness.

**FLP impossibility:** in an **asynchronous** system with **even one** faulty process, no deterministic consensus protocol can guarantee both safety and termination. The proof constructs a schedule that keeps the system in a bivalent (undecided) state forever by delaying the one critical message.

What it means practically: **every real consensus system gives up liveness, not safety.** Paxos and Raft never decide two different values; they can fail to decide at all. They dodge FLP by adding timeouts and randomization, which is to say by assuming partial synchrony — enough to make an infinite stall vanishingly unlikely without ever making it impossible.

### Paxos, compressed

Roles: **proposers** propose, **acceptors** vote, **learners** read the outcome. Decisions need a **majority quorum** (`floor(N/2)+1`), and the reason that number works is that **any two majorities intersect in at least one node** — so a new quorum always contains someone who knows about any previously chosen value.

1. **Prepare(n)** — proposer picks a number `n`, asks a majority to promise not to accept anything lower. Acceptors reply with the highest proposal they've already accepted.
2. **Accept(n, v)** — if the proposer learned of an accepted value, **it must propose that value, not its own**. Only if nobody reported one may it choose freely.
3. **Learn** — once a majority accepts `(n, v)`, `v` is chosen.

The prepare phase is not about agreeing on a value. **It exists to discover and preserve any value that might already have been chosen.** That's the whole safety argument.

**Dueling proposers** is the liveness failure: two proposers keep outbidding each other and neither finishes. Backoff or a distinguished leader makes it unlikely, never impossible (FLP again).

**Multi-Paxos** amortizes this over a _log_ of values by electing a stable leader and skipping the prepare phase for subsequent entries.

### Raft

Same guarantees as Multi-Paxos, deliberately restructured to be teachable. Two phases:

**Leader election** — nodes are follower, candidate or leader. Followers expect heartbeats; on timeout a follower becomes a candidate, increments the **term**, and requests votes. A candidate needs a **majority** to win — a plurality is not enough, which is why split votes happen and why each node uses a **randomized** election timeout to break them. At most one leader per term.

A node won't vote for a candidate whose log is behind its own. That's what guarantees the new leader already has every committed entry.

**Log replication** — clients talk only to the leader. The leader appends, then sends `AppendEntries` (piggybacked on heartbeats) carrying the new entry _and its predecessor_. A follower accepts only if it has the predecessor, which forces logs to match. Once a majority has it, the leader commits and replies. Logs are truncated by **snapshots** so a far-behind follower gets state rather than replaying everything.

The key invariant: **if two logs contain an entry with the same index and term, they are identical up to that point.**

> > Kafka's **KRaft** controller quorum is Raft, replacing the ZooKeeper dependency. The controllers elect a leader and replicate cluster metadata — topics, partitions, ISR membership — as a Raft log. Worth knowing this is _metadata_ consensus; **partition data replication is not Raft**, see below.

## Replication

- **Active** — every replica serves reads. **Primary-backup** — one node serves, others stand by.
- **State replication** — execute on one, ship the resulting state. Executes once, but state can be huge.
- **RSM (state machine replication)** — ship the _operation log_ and re-execute on every replica. Logs are small, but execution must be **deterministic** — no `now()`, no random, no map iteration order.

**Quorums**: with `N` replicas, `W` write acks and `R` read replies, you get strong consistency when `W + R > N` (the sets must overlap). `W=N, R=1` is fast reads, slow writes. `W=1, R=N` is the reverse.

**Chain replication** — nodes form a chain; writes enter at the head and propagate to the tail; the **tail** acknowledges and serves all reads. Strong consistency with the leader only ever talking to one successor, so write throughput pipelines. The cost is that reads all pile onto the tail. **CRAQ** fixes that by letting any node serve reads: each node keeps old and new versions and asks the tail whether the new one is committed.

> > **Kafka replication is leader-follower with a quorum-ish twist.** Each partition has a leader and followers; followers fetch from the leader; the **ISR** (in-sync replicas) is the set that's caught up. `acks=all` means the leader waits for all ISR members, and `min.insync.replicas` sets the floor. That combination is what actually determines durability — `acks=all` with `min.insync.replicas=1` is a false sense of security, because the ISR can shrink to just the leader and you're back to `acks=1` without noticing.
> >
> > The `acks` setting is the AMO/ALO/EO tradeoff in one config value: `0` fire-and-forget (AMO), `1` leader only (loses data if the leader dies before replicating), `all` (durable, higher latency).

## Fault tolerance and recovery

**Rollback** to a **consistent** state — which, as with snapshots, may be a state the system was never actually in.

- **Checkpointing** — periodically save state. Fast restart, expensive to take.
  - **Uncoordinated** — each node checkpoints independently. Cheap, but risks the **domino effect**: rolling one node back invalidates another's checkpoint, cascading to the beginning.
  - **Coordinated** — nodes agree on a cut. No domino effect, single checkpoint per node, but it needs a blocking protocol.
- **Logging** — record operations, replay on recovery. Small writes, slow recovery.
  - **Pessimistic** — persist before acting. Safe, high I/O.
  - **Optimistic** — act, persist later. Fast, but a crash can leave an **orphan**: a receive that's recorded without its send.
- **Combined** — checkpoint periodically, log between checkpoints, replay only from the last checkpoint. This is what every real system does.

All of this assumes **PWD (piecewise determinism)**: given the same starting state and the same sequence of msgs, a process produces the same result. Without it, replay doesn't reconstruct anything.

> > **A Kafka consumer offset is a checkpoint, and the log between checkpoints is the partition itself.** Recovery is "resume from the last committed offset and replay" — which only works because the partition is durable and replayable, and only produces correct results because the consumer is (or had better be) deterministic and idempotent.
> >
> > This is also the argument for **event sourcing** over storing only current state: with the log retained, recovery and backfill and a new consumer group reading from the beginning are all the same operation. It's why the ML.NET worker can be re-run over historical data to evaluate a new model without touching the producers.

## Distributed transactions

**2PC** — coordinator asks everyone to prepare, everyone votes, coordinator decides and tells everyone. **It blocks**: if the coordinator dies after participants voted yes, they hold locks indefinitely because they don't know the outcome and can't ask each other.

**3PC** adds a pre-commit round so participants learn that _everyone_ voted yes before anyone commits, which lets survivors decide among themselves. It guarantees liveness under fail-stop but **is not partition tolerant** — two partitions can reach opposite decisions.

**Spanner** gets strict serializability globally by making **clock uncertainty explicit**. TrueTime returns an _interval_ `[earliest, latest]` guaranteed to contain the real time, built on GPS and atomic clocks. A transaction picks commit timestamp `s = TT.now().latest`, then **waits until `TT.now().earliest > s`** before releasing locks. That deliberate wait (~2ε, single-digit ms) guarantees that any transaction starting later gets a strictly greater timestamp, which is what makes timestamps respect real-world order. **Spanner buys consistency with latency** — it doesn't beat CAP, it pays for C in the E branch of PACELC.

> > The practical alternative for a microservices pipeline is the **saga**: no distributed lock at all, just a sequence of local transactions each with a compensating action. You give up atomicity and isolation and get availability. For sensor telemetry the question rarely arises, because ingest is idempotent appends rather than a transfer between two ledgers — which is itself the design lesson. **Avoiding distributed transactions is usually a data modelling decision, not an infrastructure one.**

---

# Scale and Operations

## DQ: harvest and yield

The most useful framework I got from AOS, and the one nobody outside the course seems to use.

- **Yield (Q)** = `queries completed / queries offered`. Did you answer?
- **Harvest (D)** = `data available / total data`. Did you answer _completely_?

`D × Q` is roughly constant for a given capacity. **When you lose capacity, you choose which one degrades** — and if you don't choose, the system chooses badly for you.

- Lose a node in a **replicated** system: `D` holds (data still exists elsewhere), `Q` drops (less capacity).
- Lose a node in a **partitioned** system: `Q` can hold (you still answer), `D` drops (part of the data is gone, so answers are incomplete).

> > This is the vocabulary for graceful degradation. If the anomaly-detection worker is down, the dashboard can serve raw telemetry without anomaly annotations — **full yield, reduced harvest**, and that's a much better failure than a 503. Deciding this per-endpoint in advance is the difference between degradation and an outage. Search engines do exactly this: return results from the shards that answered rather than failing the query.

**Upgrades** are planned capacity loss and can be measured the same way: **rolling** (one node at a time, small continuous DQ loss, two versions coexist), **big flip** (half at once, large brief loss, no version mixing), **fast reboot** (all at once during a trough).

> > A k8s rolling update with `maxUnavailable` is literally the rolling strategy with the DQ loss as a tunable. The thing rolling upgrades force on you is **version compatibility** — during the roll, v1 and v2 are both consuming from the same topic, so the message schema must be forward and backward compatible. That's the real argument for a schema registry, and it's a scheduling constraint, not a serialization preference.

## Virtualization and containers

A **VMM/hypervisor** virtualizes the hardware interface so a guest OS runs unmodified.

- **Full virt** — guest untouched; privileged instructions **trap and emulate**. Historically broken on x86 because 17 instructions failed silently instead of trapping, fixed with binary translation and then properly with VT-x/AMD-V.
- **Para virt** — guest is modified to know it's virtualized and makes explicit **hypercalls**. Faster, worse compatibility.
- **Split drivers (virtio)** — frontend driver in the guest, backend in the host/service domain, sharing ring buffers. Avoids emulating hardware that doesn't exist. This is what basically all cloud I/O runs on.

**Containers are not this.** There's one kernel; isolation comes from namespaces (what you can see: PID, net, mount, user) and cgroups (what you can use: CPU, memory, I/O). No guest OS, no trap-and-emulate, near-zero overhead — and a correspondingly weaker isolation boundary, since a kernel vulnerability crosses it. gVisor and Firecracker exist to put a real boundary back for multi-tenant workloads.

> > The relevant consequence for my project: **cgroup limits are not a virtual machine**. `/proc/cpuinfo` inside the container reports the host's cores, not the quota, so any runtime or library that sizes a thread pool from "processor count" oversubscribes and gets CFS-throttled. Memory limits are worse — exceeding `limits.memory` is an OOM **kill**, not a page-out, so a GC that ran a moment too late shows up as an unexplained pod restart rather than a slow request.

## eBPF

The one piece of OS extensibility research that actually shipped, so it's the only part of the SPIN/Exokernel lineage worth keeping.

The 1990s question was: how do you let applications extend the kernel without giving up safety or paying for a context switch? SPIN's answer was to co-locate extensions in the kernel and rely on a **type-safe language** (Modula-3) for protection instead of hardware address spaces. Exokernel's was to expose raw hardware and push policy into userspace libraries. Neither shipped.

**eBPF is SPIN's answer, done properly.** You load a program into the kernel, attach it to a hook (syscall, network path, tracepoint, function entry), and it runs in kernel context at native speed with no context switch. Safety comes from a **verifier** that statically proves termination and memory safety before loading — the same "prove it safe rather than isolate it" bet SPIN made, but enforced by a checker rather than a language.

What it's used for, and why I care:

- **Observability** — attach to kernel or userspace functions and export metrics without changing or restarting the application. This is how Pixie and Parca work.
- **Networking** — Cilium replaces kube-proxy's iptables rules with eBPF programs, which is how k8s networking scales past a few thousand services.
- **Service mesh sidecar removal** — doing L7 routing in the kernel instead of proxying through a userspace Envoy per pod.
- **Security** — Falco, seccomp filtering.

That's the depth I need. The takeaway is one sentence: **eBPF is safe, verified, in-kernel extensibility, and it's why modern observability and CNI can be low-overhead.**

## Saltzer and Schroeder's design principles

1. **Economy of mechanism** — keep it small enough to verify.
2. **Fail-safe defaults** — deny by default; grant explicitly. _(A default of "allow" can't be audited.)_
3. **Complete mediation** — check every access, every time. No caching the authorization decision.
4. **Open design** — security rests on the key, not the secrecy of the design.
5. **Separation of privilege** — require two independent conditions.
6. **Least privilege** — grant only what's needed.
7. **Least common mechanism** — minimize shared machinery between users; shared state is a channel.
8. **Psychological acceptability** — if it's hard to use correctly, it will be used incorrectly.

The framing point that stuck: **state security goals positively.** "Prevent all violations" is unachievable and unfalsifiable. "Only role X may do Y" is checkable.

K8s has fail-safe defaults, complete mediation,

> > 2, 3 and 6 map directly onto k8s: default-deny NetworkPolicies, RBAC per ServiceAccount rather than per cluster, and no `latest` tags or cluster-admin bindings. 7 is the argument for a ServiceAccount per workload instead of one shared identity.

# Concept → project map

Where each idea shows up in the sensor pipeline, for when I need to answer "so what did your masters actually give you."

| Concept                             | Where it lands                                                      |
| ----------------------------------- | ------------------------------------------------------------------- |
| Bounded buffer, producer-consumer   | `Channel<T>` in the emulator; drop-and-count on full                |
| Backpressure (flow control)         | Gateway `429 + Retry-After`; TCP `rwnd` is the same idea            |
| Rate limiting (token bucket, DRR)   | Per-device quotas; shaping vs policing                              |
| Log-structured storage              | Kafka partitions, segments, compaction; SQLite WAL                  |
| Consistent hashing                  | `tractorID → partition`; ordering is per-partition                  |
| ALO + idempotency                   | Offset-commit ordering; dedupe on `(deviceId, timestamp)`           |
| Quorum replication                  | `acks=all` + `min.insync.replicas`                                  |
| Consensus (Raft)                    | KRaft controller quorum                                             |
| Checkpoint + log replay             | Consumer offsets; event sourcing for model backfill                 |
| Causality tracking                  | OTel `traceparent` across Kafka headers                             |
| Consistent global snapshot          | Reading a trace or a scrape set — a cut, not a photograph           |
| Heartbeats, fail-silent             | k8s probes; `session.timeout.ms` vs `max.poll.interval.ms`          |
| CFS scheduling                      | k8s CPU limits → throttling; thread pool sizing                     |
| Cache coherence, false sharing      | `docs/Concurrency.md` — padded counters                             |
| Page cache, `sendfile`              | Why Kafka's heap stays small                                        |
| DQ / harvest-yield                  | Serve telemetry without anomaly annotations when the worker is down |
| Rolling upgrade                     | `maxUnavailable`; schema compatibility during the roll              |
| Namespaces + cgroups                | Containers aren't VMs; `/proc/cpuinfo` lies                         |
| Fail-safe defaults, least privilege | Default-deny NetworkPolicy; ServiceAccount per workload             |
