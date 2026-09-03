check "topic_names_are_distinct" {
  assert {
    condition = length(toset([
      var.events_topic,
      var.alerts_topic,
      var.dead_letter_topic,
    ])) == 3
    error_message = "events_topic, alerts_topic, and dead_letter_topic must be distinct."
  }
}

resource "kafka_topic" "telemetry_events" {
  name               = var.events_topic
  partitions         = var.partitions
  replication_factor = var.replication_factor

  config = {
    "cleanup.policy" = "delete"
    "retention.ms"   = "604800000" # 7 days
  }
}

resource "kafka_topic" "telemetry_alerts" {
  name               = var.alerts_topic
  partitions         = var.partitions
  replication_factor = var.replication_factor

  config = {
    "cleanup.policy"            = "compact"
    "delete.retention.ms"       = "86400000"
    "min.cleanable.dirty.ratio" = "0.1"
    "segment.ms"                = "86400000"
  }
}

# Poison Kafka records must be published durably before Diagnostics commits their source offset.
# A delete policy keeps forensic evidence for a bounded period instead of growing without limit.
resource "kafka_topic" "telemetry_events_dead_letter" {
  name               = var.dead_letter_topic
  partitions         = var.partitions
  replication_factor = var.replication_factor

  config = {
    "cleanup.policy" = "delete"
    "retention.ms"   = "2592000000" # 30 days
  }
}
