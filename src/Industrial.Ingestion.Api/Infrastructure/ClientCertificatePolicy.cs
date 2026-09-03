using System.Security.Cryptography.X509Certificates;

using Industrial.Shared;

namespace Industrial.Ingestion.Api.Infrastructure;

/// <summary>
/// Validates a gateway certificate against the private CA and an explicit SHA-256 allowlist.
/// Removing one fingerprint and restarting ingestion revokes that gateway without affecting its
/// peers. A production issuer can replace the file with a dynamically managed trust policy.
/// </summary>
public sealed class ClientCertificatePolicy : IDisposable
{
    private readonly X509Certificate2 _trustedRoot;
    private readonly HashSet<string> _allowedFingerprints;

    private ClientCertificatePolicy(
        X509Certificate2 trustedRoot,
        HashSet<string> allowedFingerprints
    )
    {
        _trustedRoot = trustedRoot;
        _allowedFingerprints = allowedFingerprints;
    }

    public static ClientCertificatePolicy Load(string rootPath, string allowlistPath)
    {
        var root = X509CertificateLoader.LoadCertificateFromFile(rootPath);
        try
        {
            var allowed = File.ReadLines(allowlistPath)
                .Select(line => line.Split('#', 2)[0].Trim())
                .Where(line => line.Length > 0)
                .Select(NormalizeSha256Fingerprint)
                .ToHashSet(StringComparer.Ordinal);

            if (allowed.Count == 0)
                throw new InvalidDataException("The gateway certificate allowlist is empty.");

            return new ClientCertificatePolicy(root, allowed);
        }
        catch
        {
            root.Dispose();
            throw;
        }
    }

    public bool IsAllowed(X509Certificate2 certificate)
    {
        var fingerprint = CertificateTrust.Sha256Fingerprint(certificate);
        return _allowedFingerprints.Contains(fingerprint)
            && CertificateTrust.IsTrustedFor(
                certificate,
                _trustedRoot,
                CertificateTrust.ClientAuthenticationOid
            );
    }

    internal static string NormalizeSha256Fingerprint(string value)
    {
        var normalized = value.Replace(":", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();

        if (
            normalized.Length != 64
            || normalized.Any(character => !char.IsAsciiHexDigit(character))
        )
        {
            throw new InvalidDataException(
                "Every allowed client fingerprint must be a 64-digit SHA-256 hex value."
            );
        }

        return normalized;
    }

    public void Dispose() => _trustedRoot.Dispose();
}
