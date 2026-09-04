using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Industrial.Ingestion.Api.Infrastructure;
using Industrial.Shared;

using Microsoft.Extensions.Logging.Abstractions;

namespace Industrial.Ingestion.Tests;

public sealed class ClientCertificatePolicyTests
{
    [Fact]
    public void Custom_root_trust_requires_the_expected_eku_and_issuer()
    {
        using var trustedCa = CreateCertificateAuthority("trusted");
        using var otherCa = CreateCertificateAuthority("other");
        using var client = IssueLeaf(trustedCa, CertificateTrust.ClientAuthenticationOid);
        using var server = IssueLeaf(trustedCa, CertificateTrust.ServerAuthenticationOid);

        Assert.True(
            CertificateTrust.IsTrustedFor(
                client,
                trustedCa,
                CertificateTrust.ClientAuthenticationOid
            )
        );
        Assert.True(
            CertificateTrust.IsTrustedFor(
                server,
                trustedCa,
                CertificateTrust.ServerAuthenticationOid
            )
        );
        Assert.False(
            CertificateTrust.IsTrustedFor(
                client,
                trustedCa,
                CertificateTrust.ServerAuthenticationOid
            )
        );
        Assert.False(
            CertificateTrust.IsTrustedFor(
                client,
                otherCa,
                CertificateTrust.ClientAuthenticationOid
            )
        );
    }

    [Fact]
    public void Client_policy_requires_both_private_ca_trust_and_an_allowlisted_fingerprint()
    {
        var directory = Directory.CreateTempSubdirectory("industrial-mtls-tests-");
        try
        {
            using var trustedCa = CreateCertificateAuthority("trusted");
            using var client = IssueLeaf(
                trustedCa,
                CertificateTrust.ClientAuthenticationOid
            );
            using var otherClient = IssueLeaf(
                trustedCa,
                CertificateTrust.ClientAuthenticationOid
            );
            var caPath = Path.Combine(directory.FullName, "ca.crt");
            var allowlistPath = Path.Combine(directory.FullName, "allowed.txt");
            File.WriteAllText(caPath, trustedCa.ExportCertificatePem());
            File.WriteAllText(
                allowlistPath,
                AddColons(CertificateTrust.Sha256Fingerprint(client).ToLowerInvariant())
            );

            using var policy = ClientCertificatePolicy.Load(caPath, allowlistPath);

            Assert.True(policy.IsAllowed(client));
            Assert.False(policy.IsAllowed(otherClient));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-fingerprint")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAZ")]
    public void Invalid_allowlist_entries_fail_closed(string fingerprint)
    {
        Assert.Throws<InvalidDataException>(() =>
            ClientCertificatePolicy.NormalizeSha256Fingerprint(fingerprint)
        );
    }

    private static X509Certificate2 CreateCertificateAuthority(string name)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={name}",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(true, false, 0, critical: true)
        );
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
                critical: true
            )
        );
        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false)
        );
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30)
        );
    }

    private static X509Certificate2 IssueLeaf(X509Certificate2 issuer, string eku)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=leaf",
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
            new X509EnhancedKeyUsageExtension([new Oid(eku)], critical: false)
        );

        var serial = RandomNumberGenerator.GetBytes(16);
        return request.Create(
            issuer,
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow.AddDays(7),
            serial
        );
    }

    private static string AddColons(string value) =>
        string.Join(':', Enumerable.Range(0, value.Length / 2).Select(i => value.Substring(i * 2, 2)));
}

public sealed class ReloadingClientCertificatePolicyTests
{
    [Fact]
    public void Removing_fingerprint_revokes_gateway_without_restart()
    {
        var directory = Directory.CreateTempSubdirectory("industrial-mtls-reload-");
        try
        {
            using var trustedCa = CreateAuthority();
            using var gatewayA = IssueClient(trustedCa);
            using var gatewayB = IssueClient(trustedCa);
            var (caPath, allowlistPath) = WriteTrustFiles(
                directory,
                trustedCa,
                CertificateTrust.Sha256Fingerprint(gatewayA)
            );

            using var policy = CreateReloadingPolicy(caPath, allowlistPath);
            Assert.True(policy.IsAllowed(gatewayA));
            Assert.False(policy.IsAllowed(gatewayB));

            // Revoke A by editing the file, the same instance, no restart.
            File.WriteAllText(allowlistPath, CertificateTrust.Sha256Fingerprint(gatewayB));
            policy.ReloadIfChanged();

            Assert.False(policy.IsAllowed(gatewayA));
            Assert.True(policy.IsAllowed(gatewayB));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Invalid_edit_keeps_previous_snapshot()
    {
        var directory = Directory.CreateTempSubdirectory("industrial-mtls-reload-");
        try
        {
            using var trustedCa = CreateAuthority();
            using var gatewayA = IssueClient(trustedCa);
            var (caPath, allowlistPath) = WriteTrustFiles(
                directory,
                trustedCa,
                CertificateTrust.Sha256Fingerprint(gatewayA)
            );

            using var policy = CreateReloadingPolicy(caPath, allowlistPath);
            File.WriteAllText(allowlistPath, "not-a-fingerprint");
            policy.ReloadIfChanged();

            Assert.True(policy.IsAllowed(gatewayA));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Rotated_ca_and_allowlist_take_effect_without_restart()
    {
        var directory = Directory.CreateTempSubdirectory("industrial-mtls-reload-");
        try
        {
            using var oldCa = CreateAuthority();
            using var oldGateway = IssueClient(oldCa);
            using var newCa = CreateAuthority();
            using var newGateway = IssueClient(newCa);
            var (caPath, allowlistPath) = WriteTrustFiles(
                directory,
                oldCa,
                CertificateTrust.Sha256Fingerprint(oldGateway)
            );

            using var policy = CreateReloadingPolicy(caPath, allowlistPath);
            Assert.True(policy.IsAllowed(oldGateway));

            File.WriteAllText(caPath, newCa.ExportCertificatePem());
            File.WriteAllText(
                allowlistPath,
                CertificateTrust.Sha256Fingerprint(newGateway)
            );
            policy.ReloadIfChanged();

            Assert.False(policy.IsAllowed(oldGateway));
            Assert.True(policy.IsAllowed(newGateway));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static ReloadingClientCertificatePolicy CreateReloadingPolicy(
        string caPath,
        string allowlistPath
    ) =>
        new(
            ClientCertificatePolicy.Load(caPath, allowlistPath),
            caPath,
            allowlistPath,
            // Far future: the tests drive ReloadIfChanged explicitly.
            TimeSpan.FromHours(1),
            NullLogger<ReloadingClientCertificatePolicy>.Instance
        );

    private static (string CaPath, string AllowlistPath) WriteTrustFiles(
        DirectoryInfo directory,
        X509Certificate2 ca,
        string fingerprint
    )
    {
        var caPath = Path.Combine(directory.FullName, "ca.crt");
        var allowlistPath = Path.Combine(directory.FullName, "allowed.txt");
        File.WriteAllText(caPath, ca.ExportCertificatePem());
        File.WriteAllText(allowlistPath, fingerprint);
        return (caPath, allowlistPath);
    }

    private static X509Certificate2 CreateAuthority()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=reload-test-ca",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(true, false, 0, critical: true)
        );
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
                critical: true
            )
        );
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30)
        );
    }

    private static X509Certificate2 IssueClient(X509Certificate2 issuer)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=reload-test-gateway",
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
                [new Oid(CertificateTrust.ClientAuthenticationOid)],
                critical: false
            )
        );
        return request.Create(
            issuer,
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow.AddDays(7),
            RandomNumberGenerator.GetBytes(16)
        );
    }
}
