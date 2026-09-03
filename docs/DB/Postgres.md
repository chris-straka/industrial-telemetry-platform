# Postgres storage: heaps, pages, B+trees, and why `MessageId` is a v7 uuid

Everything here is about the write path of `TelemetryReadings`, which takes one insert
per reading forever and never updates anything. That shape is why these decisions go the
way they do; a read-heavy table would trade differently.

## The table is a heap, not a tree

Postgres stores a table as a **heap**: unordered 8KB pages, each new row dropped wherever
there is free space. There is no such thing as "the order the table is stored in". If you
want rows back in an order you say `ORDER BY` and Postgres either sorts them or walks an
index.

This is not universal, and the contrast is already inside this repo. SQLite makes an
`INTEGER PRIMARY KEY` an alias for the internal rowid, and the table itself *is* a B-tree
keyed on it — which is exactly why the gateway gets "oldest first" for free by ordering on
`Id` (see the comment in `Features/Buffer/TelemetryRecord.cs` and its use in
`UploaderWorker`). InnoDB and SQL Server do the same thing under the name *clustered
index*.

So: the edge gateway's buffer is a tree whose leaves are the rows. Postgres's
`TelemetryReadings` is a pile of pages plus three separate trees pointing into it. Do not
carry intuitions from one to the other. In particular, making `Id` a time-ordered uuid
orders the **PK index**, not the table.

## Pages

A page is 8KB. It is the unit of I/O and the unit of caching — Postgres never reads a
single row or a single index key, it reads the whole page that contains it into shared
buffers. Almost every performance claim below is really a claim about how many *distinct
pages* an operation touches and whether they were already in memory.

## B+tree anatomy

A btree index in Postgres is a **B+tree** (the Lehman & Yao variant, which allows readers
to descend without blocking against concurrent page splits). One node is exactly one page,
1:1, no exceptions.

- **Leaf pages** hold the indexed values plus pointers to the heap rows. All the actual
  data in the index lives here.
- **Internal pages** hold only separator keys and child pointers — "keys below X are in
  page 40, keys above are in page 41". No row data.
- **The root** is an internal page, unless the whole index still fits in one page.

Two properties that matter:

- **The tree is balanced**: every leaf is at the same depth. Depth is about how many pages
  you read on the way down (3–4 even for a very large index); it has nothing to do with
  sorted order. "The rightmost page" means the leaf holding the highest keys, not a leaf at
  some particular level.
- **Leaves are chained to their siblings**, so a range scan walks sideways instead of
  going back up through the root.

## Why v7 instead of v4

`Guid.NewGuid()` returns a **v4** uuid: 122 random bits. `Guid.CreateVersion7()` returns a
**v7**: a 48-bit millisecond timestamp followed by random bits, so newer ids sort higher.
The version is 4 bits stored inside the value itself.

Random keys are bad for *writes*, not reads:

- Every insert lands on a different leaf page, and at any real table size that page is
  probably not in shared buffers — so a write costs a read first.
- Inserting into the middle of a full page **splits** it into two half-full pages. Do that
  continuously and the index ends up substantially larger than its contents need.

Sequential keys avoid both. Every insert goes to the same rightmost leaf, which is
guaranteed hot because you just touched it, and pages fill completely before a new one is
started.

What this is *not*: it is not a read optimisation. A lookup is O(log n) either way. It is
also not about pages being physically adjacent on disk — the tree links them logically and
they can live anywhere.

Two indexes here take that write hit, so both got v7-shaped keys: the PK on `Id` and the
unique index on `MessageId` (which is v7 because the sensor mints it that way). The third,
`(EquipmentId, OccurredAt)`, is already time-ordered per device.

**Alternative: a sequential `bigint` identity for `Id`.** Denser than a uuid and perfectly
ordered. The database-local `Id` does not need to be minted at the sensor; `MessageId` is the
offline identity carried across services. This project keeps both as v7 UUIDs for one consistent
identifier shape, but that is a simplicity choice rather than a distributed-systems requirement.

## Why `MessageId` is `Guid` and not `string`

Postgres `uuid` is 16 bytes. The same value as `text` is 37. That difference is paid on
every row and again on every entry of the unique index, which is the index touched by
every single message that arrives.

Ordering was **not** a reason: a v7 rendered as hex sorts lexicographically in the same
order as its bytes, so `text` would have given the same insert locality. This change is
purely about size.

The wire format stays a string — protobuf has no uuid scalar, and the Kafka envelope is
JSON. The consumer parses at the boundary (`Guid.TryParse` in `TelemetryConsumerWorker`),
which folds "malformed id" into the same poison-pill path as "missing id", since neither
can be deduplicated.

**Rejected: protobuf `bytes`** for `message_id`, which would put 16 raw bytes on the wire
instead of 36 characters. Correct, and unreadable in logs and `grpcurl` output. Not worth
it until the wire is actually hot.

## Why the idempotency key is a uuid at all

The unique index on `MessageId` is the entire basis of the "duplicates = 0" claim, so what
gets put in that column is load-bearing.

**Rejected: `(EquipmentId, SequenceNumber)`.** It is already unique, already monotonic,
already carried end to end, and narrower than a uuid. It fails on restart: the emulator
resets `seq` to 0, so a restarted device re-issues sequence numbers it already used, and
those readings are silently swallowed by the unique index as "duplicates". Real firmware
would persist `seq` in NVRAM and this would become viable — but the failure mode of
getting it wrong is *silent data loss*, which is worse than a wider index.

**Rejected: a timestamp-derived key.** Two problems, either fatal. The whole point of the
key is that a *retried* request carries the *same* id; derive it from `now` and the retry
computes a new one and the duplicate sails straight through. And clocks jump backwards —
NTP correcting drift, a VM restoring from a snapshot — so the sensor re-issues ids it has
already used, and a genuine new reading collides with an old one.

A random-tailed uuid has neither problem: it is minted once, at the origin, and carried
unchanged through every hop.

## The three indexes on `TelemetryReadings`

| index | why |
| --- | --- |
| `PK_TelemetryReadings` on `Id` | EF needs a key. Never queried by hand. |
| `IX_TelemetryReadings_MessageId` UNIQUE | The deduplication guarantee. A database constraint, not application logic, so it holds even if the `AnyAsync` pre-check races. |
| `IX_TelemetryReadings_EquipmentId_OccurredAt` | The dashboard's query shape: one machine's readings over a time window, in event-time order. |

## Migration note

The original schema history was squashed into `InitialCreate` while nothing outside a throwaway
development database had applied it. Later outbox and anomaly-audit changes are ordinary appended
migrations. Once any migration is deployed to a persistent environment, renumbering or rewriting
that history would no longer be safe.

Worth knowing why it was squashed rather than appended to: EF generates `AddColumn` with a
constant `defaultValue` so existing rows have something to hold, and that default **stays
on the column afterwards**. `MessageId uuid NOT NULL DEFAULT '00000000-...'` means an
insert that forgets to set it gets `Guid.Empty` instead of an error — and the *second* such
insert trips the unique index. A `CreateTable` in a squashed migration emits no defaults at
all, so the problem disappears rather than needing a follow-up `DROP DEFAULT`. This is the
same "no fallback defaults" rule the configuration section of `AGENTS.md` applies to
`appsettings`, one layer down.
