resource "kafka_topic" "telemetry_events" {
  name               = "telemetry-events"
  partitions         = var.partitions
  replication_factor = var.replication_factor

  config = {
    "cleanup.policy" = "delete"
    "retention.ms"   = "604800000" # 7 days
  }
}

resource "kafka_topic" "telemetry_alerts" {
  name               = "telemetry-alerts"
  partitions         = var.partitions
  replication_factor = var.replication_factor

  config = {
    "cleanup.policy"        = "compact"
    "delete.retention.ms"   = "86400000"
    "min.cleanable.dirty.ratio" = "0.1"
  }
}
