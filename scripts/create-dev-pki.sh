#!/bin/sh

set -eu

AUTHORITY_DIR=${AUTHORITY_DIR:-/authority}
INGESTION_TLS_DIR=${INGESTION_TLS_DIR:-/ingestion}
EDGE_TLS_DIR=${EDGE_TLS_DIR:-/edge}
SENSOR_TLS_DIR=${SENSOR_TLS_DIR:-/sensors}
# Three emulator replicas of four devices each: EQ-0 through EQ-11.
SENSOR_DEVICE_COUNT=${SENSOR_DEVICE_COUNT:-12}
KAFKA_TLS_DIR=${KAFKA_TLS_DIR:-/kafka}
# Development-only keystore password, same class of placeholder as the Compose
# Postgres password. Production uses a managed secret, never a baked-in literal.
KAFKA_TLS_PASSWORD=${KAFKA_TLS_PASSWORD:-changeit}

mkdir -p "$AUTHORITY_DIR" "$INGESTION_TLS_DIR" "$EDGE_TLS_DIR" "$SENSOR_TLS_DIR" "$KAFKA_TLS_DIR"
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
    pfx_check_password=${3:-}
    pfx_check_certificate="$work_dir/check-$pfx_check_purpose.crt"

    openssl pkcs12 -in "$pfx_check_path" -passin "pass:$pfx_check_password" -clcerts -nokeys \
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

if ! pfx_is_current "$EDGE_TLS_DIR/server.pfx" sslserver; then
    issue_certificate \
        edge-gateway-server \
        edge-gateway \
        serverAuth \
        'DNS:edge-gateway,DNS:localhost' \
        "$EDGE_TLS_DIR" \
        server.pfx
fi

# One client identity per simulated device (CN=EQ-N). Each emulator replica mounts
# this directory and loads only its own shard, so no two devices share a secret.
i=0
while [ "$i" -lt "$SENSOR_DEVICE_COUNT" ]; do
    if ! pfx_is_current "$SENSOR_TLS_DIR/device-EQ-$i.pfx" sslclient; then
        issue_certificate \
            "sensor-EQ-$i" \
            "EQ-$i" \
            clientAuth \
            "URI:spiffe://industrial-platform/sensor/EQ-$i" \
            "$SENSOR_TLS_DIR" \
            "device-EQ-$i.pfx"
    fi
    i=$((i + 1))
done

cp "$AUTHORITY_DIR/ca.crt" "$INGESTION_TLS_DIR/ca.crt.tmp"
mv "$INGESTION_TLS_DIR/ca.crt.tmp" "$INGESTION_TLS_DIR/ca.crt"
cp "$AUTHORITY_DIR/ca.crt" "$EDGE_TLS_DIR/ca.crt.tmp"
mv "$EDGE_TLS_DIR/ca.crt.tmp" "$EDGE_TLS_DIR/ca.crt"
cp "$AUTHORITY_DIR/ca.crt" "$SENSOR_TLS_DIR/ca.crt.tmp"
mv "$SENSOR_TLS_DIR/ca.crt.tmp" "$SENSOR_TLS_DIR/ca.crt"

# Kafka brokers terminate client TLS. One server identity (SAN: kafka for the Compose
# network, localhost for host tools) plus a CA-only truststore and a static client
# properties file for the JVM tools (kafka-topics, console producer/consumer).
if ! pfx_is_current "$KAFKA_TLS_DIR/kafka-server.p12" sslserver "$KAFKA_TLS_PASSWORD"; then
    issue_certificate \
        kafka-server \
        kafka \
        serverAuth \
        'DNS:kafka,DNS:localhost' \
        "$KAFKA_TLS_DIR" \
        kafka-server.p12
    # issue_certificate mints empty-password stores; re-wrap with the Kafka password
    # so the broker and the freshness check above share one credential.
    openssl pkcs12 -in "$KAFKA_TLS_DIR/kafka-server.p12" -passin pass: -nodes \
        -out "$work_dir/kafka-server.pem" 2>/dev/null
    openssl pkcs12 -export \
        -in "$work_dir/kafka-server.pem" \
        -passout "pass:$KAFKA_TLS_PASSWORD" \
        -out "$KAFKA_TLS_DIR/kafka-server.p12.tmp" 2>/dev/null
    mv "$KAFKA_TLS_DIR/kafka-server.p12.tmp" "$KAFKA_TLS_DIR/kafka-server.p12"
fi
# PEM CA for librdkafka clients (SslCaLocation). The JVM truststore (ca.p12) is built
# by kafka-truststore-init with keytool: openssl's PKCS12 cert bags load as zero
# entries in a Java truststore, so openssl cannot mint that file.
cp "$AUTHORITY_DIR/ca.crt" "$KAFKA_TLS_DIR/ca.crt.tmp"
mv "$KAFKA_TLS_DIR/ca.crt.tmp" "$KAFKA_TLS_DIR/ca.crt"
cat > "$KAFKA_TLS_DIR/client.properties.tmp" <<EOF
security.protocol=SSL
ssl.truststore.location=/tls/ca.p12
ssl.truststore.password=$KAFKA_TLS_PASSWORD
ssl.truststore.type=PKCS12
EOF
mv "$KAFKA_TLS_DIR/client.properties.tmp" "$KAFKA_TLS_DIR/client.properties"

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
# The gateway volumes hold that process's single leaf key. Sensor replicas share one device
# directory for development simplicity but each loads only its own shard at startup.
chmod 0444 \
    "$INGESTION_TLS_DIR/server.pfx" \
    "$INGESTION_TLS_DIR/ca.crt" \
    "$INGESTION_TLS_DIR/allowed-client-sha256.txt" \
    "$EDGE_TLS_DIR/client.pfx" \
    "$EDGE_TLS_DIR/server.pfx" \
    "$EDGE_TLS_DIR/ca.crt" \
    "$SENSOR_TLS_DIR/ca.crt" \
    "$SENSOR_TLS_DIR"/device-EQ-*.pfx \
    "$KAFKA_TLS_DIR/kafka-server.p12" \
    "$KAFKA_TLS_DIR/ca.crt" \
    "$KAFKA_TLS_DIR/client.properties"
