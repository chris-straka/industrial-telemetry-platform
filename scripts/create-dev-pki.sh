#!/bin/sh

set -eu

AUTHORITY_DIR=${AUTHORITY_DIR:-/authority}
INGESTION_TLS_DIR=${INGESTION_TLS_DIR:-/ingestion}
EDGE_TLS_DIR=${EDGE_TLS_DIR:-/edge}

mkdir -p "$AUTHORITY_DIR" "$INGESTION_TLS_DIR" "$EDGE_TLS_DIR"
umask 077

if ! openssl x509 -in "$AUTHORITY_DIR/ca.crt" -checkend 604800 -noout >/dev/null 2>&1 \
    || [ ! -s "$AUTHORITY_DIR/ca.key" ]; then
    openssl genrsa -out "$AUTHORITY_DIR/ca.key" 4096
    openssl req -x509 -new -sha256 \
        -key "$AUTHORITY_DIR/ca.key" \
        -days 3650 \
        -subj '/CN=IndustrialPlatform Local Development CA' \
        -addext 'basicConstraints=critical,CA:TRUE,pathlen:0' \
        -addext 'keyUsage=critical,keyCertSign,cRLSign' \
        -out "$AUTHORITY_DIR/ca.crt"
fi

work_dir=$(mktemp -d)
trap 'rm -rf "$work_dir"' EXIT INT TERM

pfx_is_current() {
    pfx_check_path=$1
    pfx_check_purpose=$2
    pfx_check_certificate="$work_dir/check-$pfx_check_purpose.crt"

    openssl pkcs12 -in "$pfx_check_path" -passin pass: -clcerts -nokeys \
        -out "$pfx_check_certificate" >/dev/null 2>&1 \
        && openssl x509 -in "$pfx_check_certificate" -checkend 604800 -noout \
            >/dev/null 2>&1 \
        && openssl verify -CAfile "$AUTHORITY_DIR/ca.crt" \
            -purpose "$pfx_check_purpose" "$pfx_check_certificate" >/dev/null 2>&1
}

issue_certificate() {
    name=$1
    subject=$2
    extended_usage=$3
    subject_alt_name=$4
    output_dir=$5
    pfx_name=$6

    openssl genrsa -out "$work_dir/$name.key" 3072
    openssl req -new -sha256 \
        -key "$work_dir/$name.key" \
        -subj "/CN=$subject" \
        -out "$work_dir/$name.csr"
    {
        printf '%s\n' 'basicConstraints=critical,CA:FALSE'
        printf '%s\n' 'keyUsage=critical,digitalSignature,keyEncipherment'
        printf 'extendedKeyUsage=%s\n' "$extended_usage"
        printf 'subjectAltName=%s\n' "$subject_alt_name"
    } > "$work_dir/$name.ext"
    openssl x509 -req -sha256 \
        -in "$work_dir/$name.csr" \
        -CA "$AUTHORITY_DIR/ca.crt" \
        -CAkey "$AUTHORITY_DIR/ca.key" \
        -CAcreateserial \
        -days 825 \
        -extfile "$work_dir/$name.ext" \
        -out "$work_dir/$name.crt"
    openssl pkcs12 -export \
        -inkey "$work_dir/$name.key" \
        -in "$work_dir/$name.crt" \
        -certfile "$AUTHORITY_DIR/ca.crt" \
        -passout pass: \
        -out "$work_dir/$pfx_name"

    cp "$work_dir/$pfx_name" "$output_dir/$pfx_name.tmp"
    mv "$output_dir/$pfx_name.tmp" "$output_dir/$pfx_name"
}

# Keep valid leaf identities stable across ordinary Compose restarts. `docker compose down -v`
# intentionally destroys this development PKI along with every other local data volume.
if ! pfx_is_current "$INGESTION_TLS_DIR/server.pfx" sslserver; then
    issue_certificate \
        ingestion-server \
        ingestion-api \
        serverAuth \
        'DNS:ingestion-api,DNS:localhost' \
        "$INGESTION_TLS_DIR" \
        server.pfx
fi

if ! pfx_is_current "$EDGE_TLS_DIR/client.pfx" sslclient; then
    issue_certificate \
        edge-gateway-client \
        edge-gateway-local-1 \
        clientAuth \
        'URI:spiffe://industrial-platform/gateway/local-1' \
        "$EDGE_TLS_DIR" \
        client.pfx
fi

cp "$AUTHORITY_DIR/ca.crt" "$INGESTION_TLS_DIR/ca.crt.tmp"
mv "$INGESTION_TLS_DIR/ca.crt.tmp" "$INGESTION_TLS_DIR/ca.crt"
cp "$AUTHORITY_DIR/ca.crt" "$EDGE_TLS_DIR/ca.crt.tmp"
mv "$EDGE_TLS_DIR/ca.crt.tmp" "$EDGE_TLS_DIR/ca.crt"

fingerprint=$(
    openssl pkcs12 -in "$EDGE_TLS_DIR/client.pfx" -passin pass: -clcerts -nokeys 2>/dev/null \
        | openssl x509 -noout -fingerprint -sha256 \
        | cut -d= -f2 \
        | tr -d ':'
)
printf '%s\n' "$fingerprint" > "$INGESTION_TLS_DIR/allowed-client-sha256.txt.tmp"
mv "$INGESTION_TLS_DIR/allowed-client-sha256.txt.tmp" \
    "$INGESTION_TLS_DIR/allowed-client-sha256.txt"

# The private CA key stays 0600 in its own volume and is never mounted into an application.
# Application volumes contain only the one private leaf key each process needs.
chmod 0444 \
    "$INGESTION_TLS_DIR/server.pfx" \
    "$INGESTION_TLS_DIR/ca.crt" \
    "$INGESTION_TLS_DIR/allowed-client-sha256.txt" \
    "$EDGE_TLS_DIR/client.pfx" \
    "$EDGE_TLS_DIR/ca.crt"
