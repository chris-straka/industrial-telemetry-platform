using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Industrial.Sensor.EdgeGateway.Configuration;
using Industrial.Sensor.EdgeGateway.Features.Buffer;
using Industrial.Sensor.EdgeGateway.Infrastructure;
using Industrial.Shared;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Industrial.EdgeGateway.Tests;

public sealed class SensorSecurityTests
{
    [Fact]
    public void Matching_device_certificate_is_authorized()
    {
        using var ca = CreateAuthority();
        using var device = IssueDevice(ca, "EQ-0");

        Assert.Null(SensorIdentityValidator.Validate(device, ca, "EQ-0"));
    }

    [Fact]
    public void Missing_certificate_is_refused()
    {
        using var ca = CreateAuthority();

        Assert.NotNull(SensorIdentityValidator.Validate(null, ca, "EQ-0"));
    }

    [Fact]
    public void Certificate_from_another_shard_is_refused()
    {
        using var ca = CreateAuthority();
        using var device = IssueDevice(ca, "EQ-4");

        var error = SensorIdentityValidator.Validate(device, ca, "EQ-0");

        Assert.Contains("EQ-0", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Certificate_from_another_ca_is_refused()
    {
        using var ca = CreateAuthority();
        using var rogueCa = CreateAuthority();
        using var rogueDevice = IssueDevice(rogueCa, "EQ-0");

        Assert.NotNull(SensorIdentityValidator.Validate(rogueDevice, ca, "EQ-0"));
    }

    [Fact]
    public async Task Tls_handshake_and_binding_behave_end_to_end()
    {
        using var ca = CreateAuthority();
        using var server = IssueServer(ca);
        using var device = IssueDevice(ca, "EQ-0");
        // In-memory certificates through the same wiring the file loader feeds in
        // production; the loader itself is covered by the ingestion mTLS tests.
        using var state = new SensorSecurityState(
            new SensorSecurityOptions
            {
                Enabled = true,
                OpsListenPort = 0,
                ListenPort = 0,
            },
            server,
            ca
        );

        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var builder = WebApplication.CreateSlimBuilder();
            builder.Services.AddDbContext<EdgeDbContext>(options =>
                options.UseSqlite(connection)
            );
            builder.Services.AddSingleton<BufferDepth>();
            builder.Services.AddSingleton<BufferMutationGate>();
            builder.Services.AddSingleton<EdgeMetrics>();
            builder.Services.Configure<BufferOptions>(options =>
            {
                options.Path = "unused.db";
                options.MaxDepth = 100;
                options.SettledIdRetentionHours = 24;
                options.QuarantineRetentionHours = 168;
                options.QuarantineMaxRows = 100;
            });
            builder.AddSensorSecurity(state);

            var app = builder.Build();
            app.MapReceiverEndpoints();
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<EdgeDbContext>();
                await db.Database.EnsureCreatedAsync();
            }
            await app.StartAsync();
            try
            {
                var addresses = app.Services
                    .GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()!
                    .Addresses;
                var tlsRaw = addresses.FirstOrDefault(a =>
                    a.StartsWith("https:", StringComparison.Ordinal)
                );
                Assert.True(
                    tlsRaw is not null,
                    $"No HTTPS listener bound; addresses were: {string.Join(", ", addresses)}"
                );
                var tlsBase = ToConnectable(tlsRaw);
                var plainBase = ToConnectable(
                    addresses.Single(a => a.StartsWith("http:", StringComparison.Ordinal))
                );

                using var tlsClient = CreateTlsClient(tlsBase, device);
                var accepted = await tlsClient.PostAsJsonAsync(
                    "/api/local/telemetry",
                    Dto("EQ-0")
                );
                Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

                var wrongShard = await tlsClient.PostAsJsonAsync(
                    "/api/local/telemetry",
                    Dto("EQ-1")
                );
                Assert.Equal(HttpStatusCode.Forbidden, wrongShard.StatusCode);

                using var anonymousTls = CreateTlsClient(tlsBase, null);
                await Assert.ThrowsAsync<HttpRequestException>(() =>
                    anonymousTls.PostAsJsonAsync("/api/local/telemetry", Dto("EQ-0"))
                );

                using var plainClient = new HttpClient { BaseAddress = new Uri(plainBase) };
                var plain = await plainClient.PostAsJsonAsync(
                    "/api/local/telemetry",
                    Dto("EQ-0")
                );
                Assert.Equal(HttpStatusCode.Forbidden, plain.StatusCode);
            }
            finally
            {
                await app.StopAsync();
            }
            await app.DisposeAsync();
        }
    }

    private static TelemetryDto Dto(string equipmentId) =>
        new(
            Guid.CreateVersion7().ToString("D"),
            equipmentId,
            1,
            DateTimeOffset.UtcNow,
            80,
            40
        );

    // Kestrel reports wildcard listeners as [::]/0.0.0.0, which cannot be dialed.
    private static string ToConnectable(string address) =>
        address
            .Replace("[::]", "127.0.0.1", StringComparison.Ordinal)
            .Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal);

    private static HttpClient CreateTlsClient(string baseAddress, X509Certificate2? device)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        if (device is not null)
            handler.ClientCertificates.Add(device);
        return new HttpClient(handler) { BaseAddress = new Uri(baseAddress) };
    }

    private static X509Certificate2 CreateAuthority()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=sensor-test-ca",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(true, false, 0, critical: true)
        );
        var selfSigned = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30)
        );
        return Detach(selfSigned);
    }

    private static X509Certificate2 IssueDevice(X509Certificate2 issuer, string equipmentId)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={equipmentId}",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, critical: true)
        );
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature,
                critical: true
            )
        );
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                [new Oid(CertificateTrust.ClientAuthenticationOid)],
                critical: false
            )
        );
        using var leaf = request.Create(
            issuer,
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow.AddDays(7),
            RandomNumberGenerator.GetBytes(16)
        );
        // Unlike CreateSelfSigned, issuer-signed Create does not attach the subject
        // key to the returned object; bind it explicitly while the key is alive.
        using var withKey = leaf.CopyWithPrivateKey(key);
        return Detach(withKey);
    }

    private static X509Certificate2 IssueServer(X509Certificate2 issuer)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=edge-gateway",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, critical: true)
        );
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                critical: true
            )
        );
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                [new Oid(CertificateTrust.ServerAuthenticationOid)],
                critical: false
            )
        );
        using var leaf = request.Create(
            issuer,
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow.AddDays(7),
            RandomNumberGenerator.GetBytes(16)
        );
        using var withKey = leaf.CopyWithPrivateKey(key);
        return Detach(withKey);
    }

    // Detach the certificate from the one-shot RSA handles above so the returned
    // value owns a usable private key after those handles are disposed.
    private static X509Certificate2 Detach(X509Certificate2 certificate)
    {
        // DefaultKeySet: EphemeralKeySet is not supported on macOS imports.
        var detached = X509CertificateLoader.LoadPkcs12(
            certificate.Export(X509ContentType.Pfx, "test-only-password"),
            "test-only-password"
        );
        certificate.Dispose();
        return detached;
    }
}
