using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

using Industrial.Sensor.Emulator.Configuration;
using Industrial.Shared;

namespace Industrial.Sensor.Emulator.Infrastructure;

/// <summary>
/// This replica's device identities: one client certificate per simulated device plus
/// the CA that signs the gateway's server certificate. Loaded once at startup so a
/// missing shard file fails fast instead of dropping readings at send time.
/// </summary>
public sealed class DeviceCredentials : IDisposable
{
    public DeviceCredentials(
        Dictionary<string, X509Certificate2> certificates,
        X509Certificate2? trustedRoot
    )
    {
        Certificates = certificates;
        TrustedRoot = trustedRoot;
    }

    public IReadOnlyDictionary<string, X509Certificate2> Certificates { get; }

    public X509Certificate2? TrustedRoot { get; }

    public bool UsesTls => Certificates.Count > 0;

    public static DeviceCredentials LoadForShard(
        GatewayOptions gateway,
        EmulatorOptions emulator
    )
    {
        if (!gateway.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return new DeviceCredentials(new Dictionary<string, X509Certificate2>(), null);

        var trustedRoot = X509CertificateLoader.LoadCertificateFromFile(
            gateway.TrustedSensorCaPath
        );

        try
        {
            var firstDevice = emulator.ReplicaId!.Value * emulator.DeviceCount;
            var certificates = new Dictionary<string, X509Certificate2>(
                StringComparer.Ordinal
            );

            try
            {
                foreach (
                    var deviceNumber in Enumerable.Range(firstDevice, emulator.DeviceCount)
                )
                {
                    var equipmentId = $"EQ-{deviceNumber}";
                    certificates[equipmentId] =
                        X509CertificateLoader.LoadPkcs12FromFile(
                            Path.Combine(
                                gateway.ClientCertificateDirectory,
                                $"device-{equipmentId}.pfx"
                            ),
                            password: string.Empty,
                            // Development PKI uses empty PFX passwords (see create-dev-pki.sh);
                            // the secret is the file itself, kept in an isolated volume.
                            X509KeyStorageFlags.EphemeralKeySet
                        );
                }
            }
            catch
            {
                foreach (var loaded in certificates.Values)
                    loaded.Dispose();
                throw;
            }

            return new DeviceCredentials(certificates, trustedRoot);
        }
        catch
        {
            trustedRoot.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var certificate in Certificates.Values)
            certificate.Dispose();
        TrustedRoot?.Dispose();
    }
}

public static class SensorTlsHandlerFactory
{
    /// <summary>
    /// One handler chain per device: the device's own client certificate plus strict
    /// server validation against the sensor CA. The credentials object owns both
    /// certificates; the handler must not dispose them.
    /// </summary>
    public static HttpMessageHandler CreateDeviceHandler(
        X509Certificate2 deviceCertificate,
        X509Certificate2 trustedRoot
    )
    {
        var handler = new HttpClientHandler();
        handler.ClientCertificates.Add(deviceCertificate);
        handler.ServerCertificateCustomValidationCallback = (
            _,
            certificate,
            _,
            errors
        ) =>
            certificate is not null
            && !errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable)
            && !errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)
            && CertificateTrust.IsTrustedFor(
                certificate,
                trustedRoot,
                CertificateTrust.ServerAuthenticationOid
            );
        return handler;
    }
}
