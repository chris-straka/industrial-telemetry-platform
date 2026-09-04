#!/usr/bin/env bash

set -Eeuo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
VERIFY_TIMEOUT_SECONDS="${VERIFY_TIMEOUT_SECONDS:-120}"

case "$VERIFY_TIMEOUT_SECONDS" in
    ''|*[!0-9]* )
        printf 'VERIFY_TIMEOUT_SECONDS must be a positive integer.\n' >&2
        exit 2
        ;;
esac

if [ "$VERIFY_TIMEOUT_SECONDS" -lt 1 ]; then
    printf 'VERIFY_TIMEOUT_SECONDS must be a positive integer.\n' >&2
    exit 2
fi

compose() {
    docker compose --project-directory "$REPO_ROOT" "$@"
}

sensor_services=(sensor-emulator sensor-emulator-2 sensor-emulator-3)
running_sensors=()

for service in "${sensor_services[@]}"; do
    if [ -n "$(compose ps --status running --quiet "$service" 2>/dev/null)" ]; then
        running_sensors+=("$service")
    fi
done

restore_sensors() {
    exit_code=$?
    # Avoid recursive EXIT handling, but keep restoration immune to a second
    # interrupt so every emulator that was running before the audit comes back.
    trap - EXIT
    trap '' INT TERM

    if [ "${#running_sensors[@]}" -gt 0 ]; then
        printf '\nRestarting previously running sensor emulators...\n'
        if ! compose start "${running_sensors[@]}" >/dev/null; then
            printf 'Failed to restart one or more sensor emulators.\n' >&2
            if [ "$exit_code" -eq 0 ]; then
                exit_code=1
            fi
        fi
    fi

    exit "$exit_code"
}

trap restore_sensors EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

if [ "${#running_sensors[@]}" -gt 0 ]; then
    printf 'Stopping active sensor emulators to take a stable audit snapshot...\n'
    compose stop --timeout 10 "${running_sensors[@]}" >/dev/null
fi

edge_queue_depth() {
    compose exec -T edge-gateway \
        curl --fail --silent --show-error --max-time 5 http://localhost:8080/buffer \
        2>/dev/null \
        | sed -n 's/.*"queueDepth":\([0-9][0-9]*\).*/\1/p'
}

diagnostics_consumer_lag() {
    compose exec -T kafka \
        /opt/kafka/bin/kafka-consumer-groups.sh \
        --bootstrap-server kafka:9092 \
        --describe \
        --group diagnostics-group \
        2>/dev/null \
        | awk '$1 == "diagnostics-group" && $6 ~ /^[0-9]+$/ { total += $6; found = 1 }
               END { if (found) print total; else exit 1 }'
}

pending_outbox_rows() {
    compose exec -T postgres \
        psql --no-psqlrc --tuples-only --no-align \
        --username admin --dbname industrial_db \
        --command 'SELECT COUNT(*) FROM "AlertOutboxMessages" WHERE "PublishedAt" IS NULL;' \
        2>/dev/null \
        | tr -d '[:space:]'
}

printf 'Waiting for the edge queue, diagnostics consumer, and alert outbox to drain...\n'
deadline=$((SECONDS + VERIFY_TIMEOUT_SECONDS))
queue_depth=unknown
consumer_lag=unknown
pending_outbox=unknown

while [ "$SECONDS" -lt "$deadline" ]; do
    queue_depth="$(edge_queue_depth || printf 'unknown')"
    consumer_lag="$(diagnostics_consumer_lag || printf 'unknown')"
    pending_outbox="$(pending_outbox_rows || printf 'unknown')"

    if [ "$queue_depth" = '0' ] \
        && [ "$consumer_lag" = '0' ] \
        && [ "$pending_outbox" = '0' ]; then
        break
    fi

    sleep 1
done

if [ "$queue_depth" != '0' ] \
    || [ "$consumer_lag" != '0' ] \
    || [ "$pending_outbox" != '0' ]; then
    printf 'Drain timed out after %ss (edge=%s, diagnostics_lag=%s, outbox=%s).\n' \
        "$VERIFY_TIMEOUT_SECONDS" "$queue_depth" "$consumer_lag" "$pending_outbox" >&2
    exit 1
fi

printf 'edge queue depth: %s\n' "$queue_depth"
printf 'diagnostics consumer lag: %s\n' "$consumer_lag"
printf 'pending alert outbox rows: %s\n' "$pending_outbox"

compose exec -T postgres \
    psql --no-psqlrc --set ON_ERROR_STOP=1 --username admin --dbname industrial_db --quiet \
    < "$SCRIPT_DIR/verify.sql"
