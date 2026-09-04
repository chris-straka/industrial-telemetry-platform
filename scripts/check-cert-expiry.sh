#!/bin/sh
#
# Audit the development PKI for upcoming expiry. This runs inside a container
# that mounts every PKI volume (see the `make certs-check` target, which reuses
# the dev-pki-init service definition), so it sees the same files the services
# use. It needs GNU date, so run it there, not on a macOS host.
#
# Exit 0 when every certificate stays valid beyond WARN_DAYS; exit 1 otherwise.
# Prints one line per certificate: path, days remaining, and status.

set -eu

WARN_DAYS=${WARN_DAYS:-30}
NOW=$(date +%s)
FAILED=0

# BusyBox date cannot parse openssl's "Aug 31 03:10:02 2036 GMT", so normalize
# to ISO first. The enddate is always GMT; evaluate it as UTC explicitly.
month_to_number() {
    case $1 in
        Jan) printf '01' ;; Feb) printf '02' ;; Mar) printf '03' ;;
        Apr) printf '04' ;; May) printf '05' ;; Jun) printf '06' ;;
        Jul) printf '07' ;; Aug) printf '08' ;; Sep) printf '09' ;;
        Oct) printf '10' ;; Nov) printf '11' ;; Dec) printf '12' ;;
        *) printf '00' ;;
    esac
}

report() {
    display=$1
    cert=$2
    end=$(openssl x509 -in "$cert" -noout -enddate 2>/dev/null | cut -d= -f2)
    if [ -z "$end" ]; then
        printf '%-52s %s\n' "$display" UNREADABLE
        FAILED=1
        return
    fi
    # shellcheck disable=SC2086
    set -- $end
    end_epoch=$(date -u -d "$4-$(month_to_number "$1")-$2 $3" +%s)
    days=$(( (end_epoch - NOW) / 86400 ))
    if [ "$days" -lt 0 ]; then
        status=EXPIRED
    elif [ "$days" -lt "$WARN_DAYS" ]; then
        status=EXPIRING
    else
        status=OK
    fi
    printf '%-52s %5dd %s\n' "$display" "$days" "$status"
    if [ "$status" != OK ]; then
        FAILED=1
    fi
}

report_pfx() {
    display=$1
    password=${2:-}
    tmp=$(mktemp)
    if openssl pkcs12 -in "$display" -passin "pass:$password" -clcerts -nokeys \
        -out "$tmp" 2>/dev/null; then
        report "$display" "$tmp"
    else
        printf '%-52s %s\n' "$display" UNREADABLE
        FAILED=1
    fi
    rm -f "$tmp"
}

# The CA itself first: everything chains to it.
report /authority/ca.crt /authority/ca.crt

for pfx in /ingestion/server.pfx /edge/client.pfx /edge/server.pfx \
    /sensors/device-EQ-*.pfx; do
    [ -e "$pfx" ] || continue
    report_pfx "$pfx"
done

for pfx in /kafka/kafka-server.p12 /kafka/admin-client.p12 /kafka/ui-client.p12; do
    [ -e "$pfx" ] || continue
    report_pfx "$pfx" changeit
done

for crt in /kafka/ca.crt \
    /kafka-clients/*-client.crt \
    /postgres/server.crt /postgres/ca.crt; do
    [ -e "$crt" ] || continue
    report "$crt" "$crt"
done

exit "$FAILED"
