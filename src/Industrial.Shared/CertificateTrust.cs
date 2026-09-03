using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Industrial.Shared;

/// <summary>Small, deterministic X.509 policy shared by the two ends of the edge-cloud hop.</summary>
public static class CertificateTrust
{
    public const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";
    public const string ClientAuthenticationOid = "1.3.6.1.5.5.7.3.2";

    /// <summary>
    /// Builds a leaf to one explicitly configured CA and requires the intended TLS EKU. Custom
    /// root trust avoids weakening validation with an accept-any development callback.
    /// </summary>
    public static bool IsTrustedFor(
        X509Certificate2 certificate,
        X509Certificate2 trustedRoot,
        string requiredApplicationPolicyOid,
        DateTimeOffset? verificationTime = null
    )
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(trustedRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredApplicationPolicyOid);

        if (certificate.RawData.AsSpan().SequenceEqual(trustedRoot.RawData))
            return false;

        var basicConstraints = certificate
            .Extensions.OfType<X509BasicConstraintsExtension>()
            .SingleOrDefault();
        if (basicConstraints is null || basicConstraints.CertificateAuthority)
            return false;

        var keyUsage = certificate.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault();
        if (keyUsage is null || !keyUsage.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature))
            return false;

        var intendedUse = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(eku =>
            eku.EnhancedKeyUsages.Cast<Oid>().Any(oid =>
                string.Equals(oid.Value, requiredApplicationPolicyOid, StringComparison.Ordinal)
            )
        );
        if (!intendedUse)
            return false;

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(trustedRoot);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(requiredApplicationPolicyOid));
        chain.ChainPolicy.VerificationTime = (verificationTime ?? DateTimeOffset.UtcNow).UtcDateTime;

        return chain.Build(certificate);
    }

    public static string Sha256Fingerprint(X509Certificate2 certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.RawData));
}
