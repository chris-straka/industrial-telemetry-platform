# SQLite

A SQLite DB file begins with a fixed 100-byte file header
(page size, text encoding, format version, and the journal mode)
Changes to the file header are visible across all connections

default journal mode is rollback, which copies pages b4 writing to them.
It rolls back to that copy if it crashes mid-write
The writer has a lock on the file when writing so readers can't see it
In SQLite the file is the entire DB (every table, every index)
