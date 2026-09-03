using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Industrial.Ingestion.Api.Infrastructure;
using Industrial.Shared;

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
