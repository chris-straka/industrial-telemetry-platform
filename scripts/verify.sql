-- End-to-end proof for the store-and-forward demo.
--   make verify
--
-- Three numbers have to hold after you kill the cloud, kill the gateway, and
-- bring both back:
--   duplicates = 0   the unique index on MessageId did its job
--   missing    = 0   no gaps in any device's sequence, so nothing was lost
--   max_lag          how far behind event time we fell (this is the outage, measured)

\echo '== totals: duplicates must be 0 =='
SELECT COUNT(*)                                          AS ingested,
       COUNT(DISTINCT "MessageId")                       AS unique_ids,
       COUNT(*) - COUNT(DISTINCT "MessageId")            AS duplicates
FROM "TelemetryReadings";

\echo ''
\echo '== per device: missing must be 0 =='
-- Sequence numbers start at 1 and are monotonic per device, so a device that
-- produced N readings must have max_seq = N. Any shortfall is lost data.
--
-- MANUAL-% is excluded because the REST door has no sequence to assign and sends 0
-- (see IngestTelemetryEndpoint). Those rows raise COUNT(*) without raising the max,
-- so one curl would drive `missing` negative and make this check report a loss that
-- never happened. Curl with a MANUAL- id and the durability numbers stay meaningful.
SELECT "EquipmentId",
       COUNT(*)                                          AS rows,
       MAX("SequenceNumber")                             AS max_seq,
       MAX("SequenceNumber") - COUNT(*)                   AS missing
FROM "TelemetryReadings"
WHERE "EquipmentId" NOT LIKE 'MANUAL-%'
GROUP BY "EquipmentId"
ORDER BY 1;

\echo ''
\echo '== end-to-end lag (ReceivedAt - OccurredAt) =='
-- Flat in steady state. A mountain across the outage, because readings kept their
-- ORIGINAL event time while they sat in the edge buffer. If this were near zero
-- after an outage it would mean the timestamps were being stamped on arrival --
-- i.e. the demo lying to itself.
SELECT MAX(EXTRACT(EPOCH FROM ("ReceivedAt" - "OccurredAt")))::int          AS max_lag_seconds,
       AVG(EXTRACT(EPOCH FROM ("ReceivedAt" - "OccurredAt")))::numeric(10,2) AS avg_lag_seconds
FROM "TelemetryReadings";

\echo ''
\echo '== anomalies flagged by ML.NET =='
SELECT COUNT(*) FILTER (WHERE "IsAnomaly") AS anomalies,
       COUNT(*)                            AS total
FROM "TelemetryReadings";
