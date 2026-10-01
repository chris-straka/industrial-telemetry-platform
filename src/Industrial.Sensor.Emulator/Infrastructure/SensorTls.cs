using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

using Industrial.Sensor.Emulator.Configuration;
using Industrial.Shared;

namespace Industrial.Sensor.Emulator.Infrastructure;

/// <summary>
/// This replica's device client certificates and the CA that signs the gateway's certificate.
/// </summary>
/// <remarks>
/// Loaded once at startup so a missing certificate file fails fast instead of dropping readings
/// at send time.
/// </remarks>
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
                            // The development PKI uses empty PFX passwords. The file itself is the
                            // secret and lives in an isolated volume.
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
    /// Creates a handler that presents one device's certificate and validates the gateway against
    /// the sensor CA.
    /// </summary>
    /// <remarks>
    /// The credentials object owns both certificates, so the handler does not dispose them.
    /// </remarks>
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
