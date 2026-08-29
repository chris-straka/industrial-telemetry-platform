# When to NoSQL

NoSQL is faster at retrieving nonuniform data under a single document/collection.

The difference between OLTP (SQL & NoSQL) and OLAP is bigger than the difference between SQL, NoSQL.

# Two Examples

Store catalog page requires joining Products, Variants, Inventory, Categories, Pricing and Specs.
Game save state requires joining Player, Inventory, Quest Progress, World State, Stats/Attributes/Equipment, Achievements

SQL can model this data relationally in one big join across many tables.
Or it can denormalize the data and store each field in one column under one JSONB.

The big JOIN starts to slow down at 100,000 reqs/s.
NoSQL is faster than JSONB, you're recreating NoSQL basically

NoSQL is handy for polymorphic data, where you have 5000 different sensors that vary in some small way.
Postgres would handle those 5000 sensors like this typically.

| Id (UUID) | EquipmentId (String) | Timestamp (DateTime) | Payload (JSONB)                            |
| :-------- | :------------------- | :------------------- | :----------------------------------------- |
| 1         | Tractor_A            | 10:00:01             | `{"RPM": 2000, "Fuel": 80}`                |
| 2         | Moisture_01          | 10:00:02             | `{"Humidity": 14.5}`                       |
| 3         | Drone_X              | 10:00:03             | `{"Lat": 45.1, "Long": -75.2, "Alt": 100}` |

Or table would have 500 cols where most of the fields would be null.

# Schema-on

Schema-on-Write (SQL): The database enforces the rules when you save.

Schema-on-Read (NoSQL): The database accepts any JSON you throw at it.
The "schema" is only applied when your C# code reads the data (shifts validation to the code more)
Your C# code has to perform "Defensive Coding" and check if (data.HasField("Temp")) for every single record.

# Performance

NoSQL also supports scaling-out (sharding) natively without read replicas or custom sharding.

SQL runs more constraints on data than NoSQL does typically, which can slow it down a bit.
To enforce an FK "Foreign Key", SQL must perform a hidden Read on the parent table before it allows the Write on the child table.
NoSQL never does this hidden read; it just blindly saves the data, which is why it is 10x faster.

If you have a Foo table with a FK to Bar, every single insert requires the DB to check the Bar index to ensure that ID exists.
At 50,000 writes/second, you're doing 50,000 index lookups a second, and this creates lock contention.

NoSQL and Mongoose shifts the work of data validation onto your servers more.

Write Throughput: A single-node PostgreSQL instance typically handles 5,000–10,000 complex writes/sec.
A single-node NoSQL can often hit 50,000–100,000+ writes/sec on the same hardware.

Write Latency: SQL might take 5–10ms per write (due to disk syncs and constraint checks).
NoSQL often returns in <1ms because it appends to a log in memory and skips the validation.

JOINing in mongoDB is slower than SQL but you should denormalize data so it doesn't happen.

# Storage Engine

The Storage Engine (B-Tree vs. LSM-Tree)

SQL (B-Trees): Optimized for Reads.
When you write, it has to find the exact right spot in a B-tree on the disk to keep things organized.
This involves "Random I/O" which is slow.

NoSQL (LSM-Trees/Append-only logs): Optimized for Writes.
It just glues the new data to the end of a file (Sequential I/O).
Sequential writing to a disk is significantly faster than jumping around to find the "correct" spot.

# Eventual Consistency

SQL: By default, it doesn't tell the API "I'm done" until it guarantees the data is written to physical disk (fsync).
This is called a disk first acknowledgement (durable).

NoSQL: Many NoSQL DBs tell the API "I'm done" the moment the data hits the RAM buffer aka the Journal (memory mapped files)
Then they sync to the disk a few milliseconds later in the background (50-100 ms).
If the power goes out in the 50 ms window before the RAM flushes to disk, you lose the sensor data.
This is called RAM first acknowledgement.

MongoDB is only ACID if you use a transaction, otherwise it's ram first.

In SQL, a single row write is ACID
In NoSQL, they're only atomic unless you configure it to be D, or if it's a tx (ACID)

# Summary

The "10x" performance gap in NoSQL comes from...
skipping referential integrity checks (no hidden lookups),
using append-only storage engines (sequential vs. random I/O),
prioritizing ingestion speed over immediate disk-sync guarantees.

# Why I didn't go with MongoDB

For industrial/agtech, data integrity and durability are more important than write throughput.

- Referential Integrity: SQL guarantees every TelemetryReading is linked to a valid EquipmentId.

NoSQL allows "orphan" data -> saves millions of readings for EquipmentId: "Garbage-123".

In industrial settings, missing data is a liability.
If a tractor engine blows up you could lose the last 50ms of pings because they were only in RAM.
Those 50ms actually matter.
In high-speed industrial telemetry (where sensors sample at 100Hz or 1kHz), 50ms represents 5 to 50 data points.
These points show the slope of a failure (e.g., did pressure spike instantly or climb steadily?).
Losing the "smoking gun" milliseconds makes it impossible to prove the root cause in a legal or warranty dispute.
You can configure MongoDB to be durable as well and it will still write faster.

Schema-on-Write: Industrial apps require "Clean Data." If a sensor fails and sends "N/A" instead of a number,
Postgres rejects it. MongoDB saves it, which can mess up downstream software.

SQL is more compact for uniform data, because the schema is defined once in the table header.
The DB doesn't have to store field names (like "Temperature":) inside every single row.
In NoSQL, those strings are repeated millions of times, wasting disk space.
They also show up in RAM meaning, SQL data is more compact in RAM than NoSQL.

Analytic Power: Calculating "Daily averages across 1,000 tractors" is faster and more expressive in SQL (especially with window functions) than in MongoDB’s Aggregation Pipeline.

Tooling: Industrial BI tools (PowerBI, Tableau) and IoT extensions (TimescaleDB) are built specifically for the SQL/Relational ecosystem.

Since the data is flat and uniform, the schema flexibility of NoSQL provides no benefit.

# More comparisons

MongoDB IoT would be better for home IoT, AdTech (too many writes), fleet tracking, gaming, social media IoT
NoSQL is good when your write volume reaches the point where Lock Contention and Random Disk I/O (B-Trees) become a physical bottleneck.
Usually around 20,000 writes/sec on a single server, SQL starts to struggle.
NoSQL (using horizontal sharding and sequential LSM writes) can scale to millions of writes/sec by just adding more $50/month servers to the cluster.

MongoDB uses BSON (Binary JSON)
When you're doing a million writes a second, you still have to store the field names 1M times.

