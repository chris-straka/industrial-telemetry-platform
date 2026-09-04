#!/usr/bin/env bash

set -Eeuo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
BASE_COMPOSE="$REPO_ROOT/docker-compose.yaml"
E2E_COMPOSE="$REPO_ROOT/tests/e2e/docker-compose.e2e.yaml"
E2E_PROJECT_NAME="${E2E_PROJECT_NAME:-industrialplatform-e2e-$(date +%s)-$$}"
STARTUP_TIMEOUT="${E2E_STARTUP_TIMEOUT_SECONDS:-180}"

case "$E2E_PROJECT_NAME" in
    [a-z0-9]* ) ;;
    * )
        printf 'E2E_PROJECT_NAME must start with a lowercase letter or digit.\n' >&2
        exit 2
        ;;
esac

case "$E2E_PROJECT_NAME" in
    *[!a-z0-9_-]* )
        printf 'E2E_PROJECT_NAME may contain only lowercase letters, digits, hyphens, and underscores.\n' >&2
        exit 2
        ;;
esac

case "$STARTUP_TIMEOUT" in
    ''|*[!0-9]* )
        printf 'E2E_STARTUP_TIMEOUT_SECONDS must be a positive integer.\n' >&2
        exit 2
        ;;
esac

if [ "$STARTUP_TIMEOUT" -lt 1 ]; then
    printf 'E2E_STARTUP_TIMEOUT_SECONDS must be a positive integer.\n' >&2
    exit 2
fi

compose() {
    docker compose \
        --project-directory "$REPO_ROOT" \
        --project-name "$E2E_PROJECT_NAME" \
        --file "$BASE_COMPOSE" \
        --file "$E2E_COMPOSE" \
        "$@"
}

log() {
    printf '\n==> %s\n' "$*"
}

fail() {
    printf '\nE2E FAILED: %s\n' "$*" >&2
    return 1
}

if ! command -v docker >/dev/null 2>&1; then
    printf 'docker is required.\n' >&2
    exit 2
fi

if ! docker compose version >/dev/null 2>&1; then
    printf 'the Docker Compose plugin is required.\n' >&2
    exit 2
fi

if ! docker info >/dev/null 2>&1; then
    printf 'the Docker daemon is not reachable. Start Docker Desktop and retry.\n' >&2
    exit 2
fi

# Never adopt (and later remove) containers from a pre-existing project name. The generated name
# is unique; this mainly protects callers who set E2E_PROJECT_NAME themselves.
if [ -n "$(docker ps -aq --filter "label=com.docker.compose.project=$E2E_PROJECT_NAME")" ]; then
    printf 'Compose project %s already has containers; choose another E2E_PROJECT_NAME.\n' \
        "$E2E_PROJECT_NAME" >&2
    exit 2
fi

cleanup() {
    exit_code=$?
    trap - EXIT INT TERM
    set +e

    if [ "$exit_code" -ne 0 ]; then
        printf '\n--- E2E container state ---\n' >&2
        compose ps -a >&2
        printf '\n--- bounded E2E logs ---\n' >&2
        compose logs --no-color --tail=200 \
            dev-pki-init edge-gateway ingestion-api diagnostics-worker postgres kafka >&2
    fi

    printf '\n==> Removing isolated Compose project %s\n' "$E2E_PROJECT_NAME"
    compose down --volumes --remove-orphans --timeout 10 >/dev/null 2>&1
    exit "$exit_code"
}

trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

container_state() {
    service=$1
    container_id="$(compose ps -q "$service" 2>/dev/null)"
    if [ -z "$container_id" ]; then
        printf 'missing'
        return
    fi

    docker inspect \
        --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' \
        "$container_id" 2>/dev/null || printf 'missing'
}

wait_for_container_health() {
    service=$1
    timeout=$2
    deadline=$((SECONDS + timeout))

    while [ "$SECONDS" -lt "$deadline" ]; do
        state="$(container_state "$service")"
        case "$state" in
            healthy)
                return 0
                ;;
            exited|dead)
                fail "$service exited before becoming healthy"
                return 1
                ;;
        esac
        sleep 1
    done

    fail "$service did not become healthy within ${timeout}s (last state: $state)"
}

kafka_init_succeeded() {
    container_id="$(compose ps -aq kafka-init 2>/dev/null)"
    [ -n "$container_id" ] || return 1
    [ "$(docker inspect --format '{{.State.Status}}' "$container_id" 2>/dev/null)" = 'exited' ] \
        || return 1
    [ "$(docker inspect --format '{{.State.ExitCode}}' "$container_id" 2>/dev/null)" = '0' ]
}

wait_for_http_from_edge() {
    description=$1
    url=$2
    timeout=$3
    deadline=$((SECONDS + timeout))

    while [ "$SECONDS" -lt "$deadline" ]; do
        if compose exec -T edge-gateway curl --fail --silent --show-error \
            --max-time 5 "$url" >/dev/null 2>&1; then
            return 0
        fi
        sleep 1
    done

    fail "$description did not become ready within ${timeout}s"
}

wait_until() {
    description=$1
    timeout=$2
    shift 2
    deadline=$((SECONDS + timeout))

    while [ "$SECONDS" -lt "$deadline" ]; do
        if "$@"; then
            return 0
        fi
        sleep 1
    done

    fail "$description did not become true within ${timeout}s"
}

edge_queue_depth() {
    response="$(
        compose exec -T edge-gateway \
            curl --fail --silent --show-error --max-time 5 http://localhost:8080/buffer \
            2>/dev/null || true
    )"
    printf '%s' "$response" | sed -n 's/.*"queueDepth":\([0-9][0-9]*\).*/\1/p'
}

edge_queue_is() {
    expected=$1
    [ "$(edge_queue_depth)" = "$expected" ]
}

post_edge_reading() {
    message_id=$1
    sequence_number=$2
    occurred_at="$(date -u +'%Y-%m-%dT%H:%M:%SZ')"
    payload="{\"messageId\":\"$message_id\",\"equipmentId\":\"E2E-GATEWAY\",\"sequenceNumber\":$sequence_number,\"occurredAt\":\"$occurred_at\",\"engineTemperature\":80.0,\"oilPressure\":40.0}"

    status="$(
        compose exec -T edge-gateway curl --silent --show-error \
            --output /dev/null --write-out '%{http_code}' --max-time 10 \
            --header 'Content-Type: application/json' \
            --data "$payload" \
            http://localhost:8080/api/local/telemetry
    )"

    if [ "$status" != '202' ]; then
        fail "gateway returned HTTP $status for MessageId $message_id"
        return 1
    fi
}

query_db() {
    sql=$1
    compose exec -T postgres \
        psql --no-psqlrc --set ON_ERROR_STOP=1 --tuples-only --no-align \
        --username admin --dbname industrial_db --command "$sql" \
        | tr -d '[:space:]'
}

database_has_unique_ids() {
    expected_count=$1
    id_list=$2
    result="$(
        query_db \
            "SELECT COUNT(*) || '|' || COUNT(DISTINCT \"MessageId\") FROM \"TelemetryReadings\" WHERE \"MessageId\" IN ($id_list);" \
            2>/dev/null || true
    )"
    [ "$result" = "$expected_count|$expected_count" ]
}

diagnostics_logged_retry_since() {
    since=$1
    output="$(
        compose logs --no-color --since "$since" diagnostics-worker 2>&1 || true
    )"
    case "$output" in
        *'it was rewound for retry.'*) return 0 ;;
        *) return 1 ;;
    esac
}

outbox_attempted() {
    message_id=$1
    attempts="$(
        query_db \
            "SELECT \"AttemptCount\" FROM \"AlertOutboxMessages\" WHERE \"MessageId\" = '$message_id'::uuid;" \
            2>/dev/null || true
    )"
    case "$attempts" in
        ''|*[!0-9]*) return 1 ;;
        *) [ "$attempts" -ge 1 ] ;;
    esac
}

outbox_is_published() {
    message_id=$1
    [ "$(
        query_db \
            "SELECT CASE WHEN \"PublishedAt\" IS NOT NULL THEN 1 ELSE 0 END FROM \"AlertOutboxMessages\" WHERE \"MessageId\" = '$message_id'::uuid;" \
            2>/dev/null || true
    )" = '1' ]
}

outbox_attempt_count() {
    message_id=$1
    query_db \
        "SELECT COALESCE(MAX(\"AttemptCount\"), -1) FROM \"AlertOutboxMessages\" WHERE \"MessageId\" = '$message_id'::uuid;" \
        2>/dev/null || true
}

outbox_failed_then_pending() {
    message_id=$1
    attempts="$(outbox_attempt_count "$message_id")"
    case "$attempts" in
        ''|*[!0-9]*) return 1 ;;
    esac
    [ "$attempts" -ge 1 ] && ! outbox_is_published "$message_id"
}

tls_volume_name() {
    printf '%s_ingestion_tls_data' "$E2E_PROJECT_NAME"
}

read_gateway_allowlist() {
    # Ingestion mounts its TLS volume read-only, so the allowlist is edited through
    # a transient helper container holding the same named volume read-write.
    docker run --rm --volume "$(tls_volume_name):/tls:ro" alpine:3.23 \
        cat /tls/allowed-client-sha256.txt
}

write_gateway_allowlist() {
    docker run --rm --interactive --volume "$(tls_volume_name):/tls" alpine:3.23 \
        sh -c 'cat > /tls/allowed-client-sha256.txt'
}


log "Validating isolated Compose model"
compose config --quiet
printf 'Compose project: %s\n' "$E2E_PROJECT_NAME"

if [ "${E2E_SKIP_BUILD:-0}" != '1' ]; then
    log 'Building E2E application images'
    compose build ingestion-api edge-gateway diagnostics-worker
else
    log 'Reusing existing E2E application images'
fi

log 'Starting Postgres and Kafka'
compose up --detach postgres kafka-init
wait_for_container_health postgres "$STARTUP_TIMEOUT"
wait_for_container_health kafka "$STARTUP_TIMEOUT"
# kafka-init has no healthcheck; its successful one-shot state is exited with status zero.
wait_until 'Kafka topic initialization' "$STARTUP_TIMEOUT" kafka_init_succeeded

log 'Starting ingestion, edge, and diagnostics'
compose up --detach ingestion-api edge-gateway diagnostics-worker
wait_for_container_health edge-gateway "$STARTUP_TIMEOUT"
wait_for_http_from_edge 'edge liveness' \
    http://localhost:8080/health "$STARTUP_TIMEOUT"
wait_for_http_from_edge 'ingestion readiness' \
    http://ingestion-api:8080/health/ready "$STARTUP_TIMEOUT"
wait_for_http_from_edge 'diagnostics readiness' \
    http://diagnostics-worker:8080/health/ready "$STARTUP_TIMEOUT"

log 'Checking that ingestion rejects a TLS client without a gateway certificate'
if compose exec -T edge-gateway curl --silent --show-error \
    --output /dev/null --max-time 10 \
    --cacert /tls/ca.crt \
    https://ingestion-api:8081/ >/dev/null 2>&1; then
    fail 'ingestion completed a TLS request that supplied no gateway client certificate'
fi

MESSAGE_ID_1='11111111-1111-4111-8111-111111111111'
MESSAGE_ID_2='22222222-2222-4222-8222-222222222222'
MESSAGE_ID_3='33333333-3333-4333-8333-333333333333'
MESSAGE_ID_4='44444444-4444-4444-8444-444444444444'
OUTBOX_MESSAGE_ID='55555555-5555-4555-8555-555555555555'
READING_IDS_SQL="'$MESSAGE_ID_1'::uuid,'$MESSAGE_ID_2'::uuid,'$MESSAGE_ID_3'::uuid"

log 'Stopping ingestion and filling the durable edge queue'
compose stop --timeout 10 ingestion-api
post_edge_reading "$MESSAGE_ID_1" 1
post_edge_reading "$MESSAGE_ID_2" 2
post_edge_reading "$MESSAGE_ID_3" 3
wait_until 'edge queue depth to reach three' 15 edge_queue_is 3

log 'Restarting the edge during the outage and checking SQLite recovery'
compose restart --timeout 10 edge-gateway
wait_for_container_health edge-gateway "$STARTUP_TIMEOUT"
wait_until 'three queued rows to survive the edge restart' 15 edge_queue_is 3

log 'Restoring ingestion and checking queue drain plus Postgres idempotency'
compose start ingestion-api
wait_for_http_from_edge 'ingestion readiness after restart' \
    http://ingestion-api:8080/health/ready "$STARTUP_TIMEOUT"
wait_until 'edge queue to drain' 60 edge_queue_is 0
wait_until 'three unique telemetry rows in Postgres' 60 \
    database_has_unique_ids 3 "$READING_IDS_SQL"

log 'Stopping Postgres and proving Diagnostics rewinds the consumed record'
POSTGRES_OUTAGE_STARTED="$(date -u +'%Y-%m-%dT%H:%M:%SZ')"
compose stop --timeout 10 postgres
post_edge_reading "$MESSAGE_ID_4" 4
wait_until 'gateway delivery to Kafka during the Postgres outage' 45 edge_queue_is 0
wait_until 'Diagnostics retry log during the Postgres outage' 45 \
    diagnostics_logged_retry_since "$POSTGRES_OUTAGE_STARTED"

log 'Restoring Postgres and checking the rewound record is persisted once'
compose start postgres
wait_for_container_health postgres "$STARTUP_TIMEOUT"
wait_until 'rewound telemetry row to persist exactly once' 60 \
    database_has_unique_ids 1 "'$MESSAGE_ID_4'::uuid"
wait_for_http_from_edge 'diagnostics readiness after Postgres recovery' \
    http://diagnostics-worker:8080/health/ready "$STARTUP_TIMEOUT"

log 'Publishing malformed Kafka JSON and checking the durable DLQ copy'
MALFORMED_TOKEN="e2e-malformed-$E2E_PROJECT_NAME"
MALFORMED_PAYLOAD="{\"token\":\"$MALFORMED_TOKEN\""
compose exec -T kafka \
    /opt/kafka/bin/kafka-console-producer.sh \
    --bootstrap-server kafka:9092 --topic telemetry-events <<< "$MALFORMED_PAYLOAD"

if ! DLQ_RECORD="$(
    compose exec -T kafka \
        /opt/kafka/bin/kafka-console-consumer.sh \
        --bootstrap-server kafka:9092 \
        --topic telemetry-events-dlq \
        --from-beginning --max-messages 1 --timeout-ms 45000 \
        2>/dev/null
)"; then
    fail 'no dead-letter record was consumed within 45s'
fi

case "$DLQ_RECORD" in
    *'"sourceTopic":"telemetry-events"'* ) ;;
    * ) fail 'DLQ record did not identify telemetry-events as its source' ;;
esac
case "$DLQ_RECORD" in
    *'"failureReason":"payload is not valid telemetry JSON"'* ) ;;
    * ) fail 'DLQ record did not retain the poison-message reason' ;;
esac
case "$DLQ_RECORD" in
    *"$MALFORMED_TOKEN"* ) ;;
    * ) fail 'DLQ record did not retain the malformed source payload' ;;
esac

log 'Stopping Kafka and checking the alert outbox survives until broker recovery'
compose stop --timeout 15 kafka
# PascalCase keys match TelemetryAlertEnvelope as the real producer serializes it.
# Anything else would not bind on read and must publish through unchanged.
OUTBOX_PAYLOAD="{\"MessageId\":\"$OUTBOX_MESSAGE_ID\",\"EquipmentId\":\"E2E-OUTBOX\",\"OccurredAt\":\"$(date -u +'%Y-%m-%dT%H:%M:%SZ')\",\"EngineTemperature\":80.0,\"Diagnostics\":\"e2e recovery\"}"
query_db \
    "INSERT INTO \"AlertOutboxMessages\" (\"Id\", \"MessageId\", \"EquipmentId\", \"Payload\", \"TraceParent\", \"CreatedAt\", \"PublishedAt\", \"AttemptCount\", \"LastError\") VALUES ('66666666-6666-4666-8666-666666666666'::uuid, '$OUTBOX_MESSAGE_ID'::uuid, 'E2E-OUTBOX', '$OUTBOX_PAYLOAD', NULL, NOW(), NULL, 0, NULL);" \
    >/dev/null
wait_until 'outbox publish attempt while Kafka is unavailable' 60 \
    outbox_attempted "$OUTBOX_MESSAGE_ID"

log 'Restoring Kafka and checking the pending alert is acknowledged'
compose start kafka
wait_for_container_health kafka "$STARTUP_TIMEOUT"
wait_until 'outbox row to publish after Kafka recovery' 90 \
    outbox_is_published "$OUTBOX_MESSAGE_ID"

log 'Freezing Kafka mid-publish to manufacture an ambiguous acknowledgement'
AMBIG_OUTBOX_MESSAGE_ID='77777777-7777-4777-8777-777777777777'
AMBIG_OUTBOX_PAYLOAD="{\"MessageId\":\"$AMBIG_OUTBOX_MESSAGE_ID\",\"EquipmentId\":\"E2E-AMBIG\",\"OccurredAt\":\"$(date -u +'%Y-%m-%dT%H:%M:%SZ')\",\"EngineTemperature\":80.0,\"Diagnostics\":\"e2e ambiguous ack\"}"
KAFKA_CONTAINER="$(compose ps -q kafka)"
if [ -z "$KAFKA_CONTAINER" ]; then
    fail 'Kafka container id is unknown; cannot freeze the broker'
fi
# Freeze first so the publisher's produce blocks against a broker that may
# already hold the request. Unlike the stopped-broker test above, the client
# failure here cannot prove the broker has nothing: the bytes may sit in the
# frozen broker's TCP buffer and be appended once it thaws. That unknown is
# exactly what makes the acknowledgement ambiguous.
docker pause "$KAFKA_CONTAINER" >/dev/null
query_db \
    "INSERT INTO \"AlertOutboxMessages\" (\"Id\", \"MessageId\", \"EquipmentId\", \"Payload\", \"TraceParent\", \"CreatedAt\", \"PublishedAt\", \"AttemptCount\", \"LastError\") VALUES ('88888888-8888-4888-8888-888888888888'::uuid, '$AMBIG_OUTBOX_MESSAGE_ID'::uuid, 'E2E-AMBIG', '$AMBIG_OUTBOX_PAYLOAD', NULL, NOW(), NULL, 0, NULL);" \
    >/dev/null
# The shared producer times out after 20s, so a failed-but-still-pending row
# proves the client gave up without knowing whether the broker persisted it.
wait_until 'outbox produce to fail ambiguously while Kafka is frozen' 90 \
    outbox_failed_then_pending "$AMBIG_OUTBOX_MESSAGE_ID"
docker unpause "$KAFKA_CONTAINER" >/dev/null
wait_for_container_health kafka "$STARTUP_TIMEOUT"

log 'Checking the ambiguous publish still delivers at least once without duplicating Postgres rows'
wait_until 'ambiguous outbox row to publish after the broker thaws' 90 \
    outbox_is_published "$AMBIG_OUTBOX_MESSAGE_ID"
# Poll the topic directly instead of wait_until so a failure reports the last
# consumer output instead of only a timeout.
alert_deadline=$((SECONDS + 60))
while true; do
    AMBIG_CONSUMER_OUTPUT="$(
        compose exec -T kafka \
            /opt/kafka/bin/kafka-console-consumer.sh \
            --bootstrap-server kafka:9092 \
            --topic telemetry-alerts \
            --from-beginning --timeout-ms 15000 \
            2>&1 || true
    )"
    AMBIG_ALERT_COUNT="$(printf '%s' "$AMBIG_CONSUMER_OUTPUT" | grep -c "$AMBIG_OUTBOX_MESSAGE_ID" || true)"
    case "$AMBIG_ALERT_COUNT" in
        ''|*[!0-9]*) AMBIG_ALERT_COUNT=0 ;;
    esac
    if [ "$AMBIG_ALERT_COUNT" -ge 1 ]; then
        break
    fi
    if [ "$SECONDS" -ge "$alert_deadline" ]; then
        printf 'Last telemetry-alerts consumer output:\n%s\n' "$AMBIG_CONSUMER_OUTPUT" >&2
        fail "ambiguous alert to reach telemetry-alerts did not become true within 60s (last count: $AMBIG_ALERT_COUNT)"
    fi
    sleep 2
done
printf 'Ambiguous-ACK alert copies in telemetry-alerts for %s: %s (at-least-once requires >= 1).\n' \
    "$AMBIG_OUTBOX_MESSAGE_ID" "$AMBIG_ALERT_COUNT"
printf 'Every copy shares one MessageId, which is the key the dashboard dedupes on (bounded remember() set plus key={MessageId}), so replays render a single item.\n'
wait_until 'earlier telemetry rows to remain exactly once after the ambiguous publish' 60 \
    database_has_unique_ids 3 "$READING_IDS_SQL"
wait_until 'rewound telemetry row to remain exactly once after the ambiguous publish' 60 \
    database_has_unique_ids 1 "'$MESSAGE_ID_4'::uuid"

log 'Revoking the gateway certificate without restarting ingestion'
REVOKE_MESSAGE_ID='aaaaaaaa-aaaa-4aaa-aaaa-aaaaaaaaaaaa'
ALLOWLIST_BACKUP="$(read_gateway_allowlist)" || fail 'could not read the gateway allowlist'
if [ -z "$ALLOWLIST_BACKUP" ]; then
    fail 'gateway allowlist is empty; nothing to revoke'
fi
# 64 hex zeros: well-formed, so the reload applies it, and it matches no gateway.
printf '0000000000000000000000000000000000000000000000000000000000000000\n' \
    | write_gateway_allowlist || fail 'could not revoke the gateway allowlist'
# Let ingestion reload before the gateway's next handshake; otherwise the first
# post-revocation attempt could still race the old snapshot on a warm lookup.
sleep 8
# Fresh TLS handshake as the now-revoked identity. The gateway itself restarts;
# ingestion does not. Its SQLite queue must survive this restart too.
compose restart --timeout 10 edge-gateway
wait_for_container_health edge-gateway "$STARTUP_TIMEOUT"
post_edge_reading "$REVOKE_MESSAGE_ID" 5
# Longer than the 5s e2e reload interval: the row must still be queued because
# every upload handshake is now refused, and refused is neither accepted nor
# rejected, so the gateway retains it.
sleep 20
if [ "$(edge_queue_depth)" != '1' ]; then
    fail 'revoked gateway upload left the edge queue (expected exactly the 1 retained row)'
fi
printf 'Revoked gateway retained its reading instead of uploading it.\n'

log 'Restoring the allowlist and checking the retained row drains without a restart'
printf '%s\n' "$ALLOWLIST_BACKUP" \
    | write_gateway_allowlist || fail 'could not restore the gateway allowlist'
wait_until 'edge queue to drain after allowlist restore' 60 edge_queue_is 0
wait_until 'revoked-then-restored reading to persist exactly once' 60 \
    database_has_unique_ids 1 "'$REVOKE_MESSAGE_ID'::uuid"

log 'All failure/recovery assertions passed'
printf 'Verified 5 telemetry MessageIds, one DLQ record, one recovered outbox publish, one ambiguous-ACK outbox recovery, and one restart-free gateway revocation.\n'
