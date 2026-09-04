using System.ComponentModel.DataAnnotations;

using Industrial.Diagnostics.Worker.Configuration;

namespace Industrial.Diagnostics.Tests;

public sealed class KafkaOptionsValidationTests
{
    [Fact]
    public void Tls_without_a_client_identity_is_rejected_before_any_broker_call()
    {
        var options = ValidTlsOptions();
        options.SslCertificateLocation = string.Empty;
        options.SslKeyLocation = string.Empty;

        var results = Validate(options);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(KafkaOptions.SslCertificateLocation)));
        Assert.Contains(results, r => r.MemberNames.Contains(nameof(KafkaOptions.SslKeyLocation)));
    }

    [Fact]
    public void Tls_with_a_client_identity_is_accepted()
    {
        Assert.Empty(Validate(ValidTlsOptions()));
    }

    [Fact]
    public void Plaintext_needs_no_client_identity()
    {
        var options = ValidTlsOptions();
        options.UseTls = false;
        options.SslCaLocation = string.Empty;
        options.SslCertificateLocation = string.Empty;
        options.SslKeyLocation = string.Empty;

        Assert.Empty(Validate(options));
    }

    private static KafkaOptions ValidTlsOptions() =>
        new()
        {
            BootstrapServers = "kafka:9092",
            UseTls = true,
            SslCaLocation = "/kafka-tls/ca.crt",
            SslCertificateLocation = "/kafka-clients/diagnostics-client.crt",
            SslKeyLocation = "/kafka-clients/diagnostics-client.key",
            GroupId = "diagnostics-tests",
            EventsTopic = "telemetry-events",
            AlertsTopic = "telemetry-alerts",
            DeadLetterTopic = "telemetry-events-dlq",
            ReadinessTimeoutSeconds = 3,
        };

    private static List<ValidationResult> Validate(KafkaOptions options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, true);
        return results;
    }
}
