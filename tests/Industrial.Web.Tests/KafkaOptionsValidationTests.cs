using System.ComponentModel.DataAnnotations;

using Industrial.Web.Api.Configuration;

namespace Industrial.Web.Tests;

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
            SslCertificateLocation = "/kafka-clients/webapi-client.crt",
            SslKeyLocation = "/kafka-clients/webapi-client.key",
            GroupId = "web-api-tests",
            EventsTopic = "telemetry-events",
            AlertsTopic = "telemetry-alerts",
            ReadinessTimeoutSeconds = 3,
        };

    private static List<ValidationResult> Validate(KafkaOptions options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, true);
        return results;
    }
}
