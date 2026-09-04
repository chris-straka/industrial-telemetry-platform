# Alert tuning: one measured drill

Thresholds in `monitoring/prometheus-alerts.yaml` were engineering defaults until
someone timed them. This records the first timing run so future edits argue with
data instead of taste.

## Method

Dev stack, fleet of 12 emulators flowing, metrics via OTel → Prometheus
(scrape 30s, evaluation 30s) → Alertmanager (group_wait 30s) → MailHog.
`docker compose stop ingestion-api` at T0, restore after the email lands, watch
`ALERTS{alertname="EdgeCloudUnreachable"}` and the inbox.

## Measured timeline (2026-09-04)

| event | time | T0+ |
| --- | --- | --- |
| ingestion stopped | 23:48:37Z | 0m00s |
| `edge_cloud_reachable == 0` observed | ≤23:51:09Z | ≤2m32s |
| `[warning] EdgeCloudUnreachable` email in MailHog | 23:51:28Z | 2m51s |
| ingestion restored | 23:53:56Z | 5m19s |
| alert resolved, edge queue depth 0 | ≤23:56:32Z | ≤7m55s |

The `for: 2m` window dominates time-to-notify; scrape, evaluation, and
group_wait add roughly a minute combined. No flapping, no duplicate emails.

## Verdicts

- `EdgeCloudUnreachable` (warning, `for: 2m`): keep. Clean fire, clean resolve,
  total under three minutes to the inbox.
- `AlertOutboxBacklogged` (`for: 10m`): keep. A 6-row restart backlog during this
  same session drained in under two minutes without firing — the window absorbs
  restart noise as designed.
- `EdgeQueueGrowingDuringCloudOutage` (`deriv[10m]`, `for: 5m`): keep with a
  known blind window. It cannot fire before ~15 minutes into an outage, so this
  5-minute drill never exercised it; it is a slow-burn signal, not a pager.

## Not yet measured

Sustained-load behavior (histogram buckets, p95/p99 lag thresholds), the
sensor-drop rules under real acquisition pressure, and a >15-minute outage that
actually trips the slow-burn rules. Those still need a load drill, not a review.
