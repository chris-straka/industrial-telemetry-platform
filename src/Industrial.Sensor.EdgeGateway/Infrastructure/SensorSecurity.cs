using System.Net;
using System.Security.Cryptography.X509Certificates;

using Industrial.Sensor.EdgeGateway.Configuration;
using Industrial.Shared;

namespace Industrial.Sensor.EdgeGateway.Infrastructure;

/// <summary>
/// This gateway's server certificate and the CA that signs device certificates.
/// </summary>
/// <remarks>
/// Registered even when sensor mTLS is disabled, so the receiver endpoint can depend on one type.
/// The container disposes both certificates.
/// </remarks>
public sealed class SensorSecurityState : IDisposable
{
    public SensorSecurityState(
        SensorSecurityOptions options,
        X509Certificate2? serverCertificate,
        X509Certificate2? trustedRoot
    )
    {
        Options = options;
        ServerCertificate = serverCertificate;
        TrustedRoot = trustedRoot;
    }

    public SensorSecurityOptions Options { get; }

    public X509Certificate2? ServerCertificate { get; }

    public X509Certificate2? TrustedRoot { get; }

    public void Dispose()
    {
        ServerCertificate?.Dispose();
        TrustedRoot?.Dispose();
    }
}

public static class SensorSecurityExtensions
{
    public static void AddSensorSecurity(
        this WebApplicationBuilder builder,
        SensorSecurityOptions options
    )
    {
        if (!options.Enabled)
        {
            builder.Services.AddSingleton(
                new SensorSecurityState(options, null, null)
            );
            return;
        }

        var serverCertificate = X509CertificateLoader.LoadPkcs12FromFile(
            options.ServerCertificatePath,
            options.ServerCertificatePassword,
            X509KeyStorageFlags.EphemeralKeySet
        );

        SensorSecurityState? state = null;

        try
        {
            var trustedRoot = X509CertificateLoader.LoadCertificateFromFile(
                options.TrustedSensorCaPath
            );
            state = new SensorSecurityState(options, serverCertificate, trustedRoot);
            builder.AddSensorSecurity(state);
        }
        catch
        {
            // Once the state is constructed it owns both certificates. Until then this method
            // must dispose the server certificate itself.
            if (state is null)
                serverCertificate.Dispose();
            else
                state.Dispose();
            throw;
        }
    }

    // Wires Kestrel and DI from an already-built state. Tests use this with in-memory
    // certificates to exercise the same handshake and route path without the filesystem
    // loader, whose EphemeralKeySet is Linux-only.
    internal static void AddSensorSecurity(
        this WebApplicationBuilder builder,
        SensorSecurityState state
    )
    {
        if (!state.Options.Enabled)
        {
            builder.Services.AddSingleton(state);
            return;
        }

        var serverCertificate =
            state.ServerCertificate
            ?? throw new InvalidOperationException(
                "SensorSecurity is enabled but has no server certificate."
            );
        var trustedRoot =
            state.TrustedRoot
            ?? throw new InvalidOperationException(
                "SensorSecurity is enabled but has no trusted sensor CA."
            );

        builder.Services.AddSingleton(state);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            // Explicit code endpoints suppress the image's default binding, so the ops
            // listener is bound here too whenever the sensor listener exists.
            kestrel.ListenAnyIP(state.Options.OpsListenPort);
            kestrel.ListenAnyIP(
                state.Options.ListenPort,
                listen =>
                    listen.UseHttps(https =>
                    {
                        https.ServerCertificate = serverCertificate;
                        https.ClientCertificateMode =
                            Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode.RequireCertificate;
                        https.ClientCertificateValidation = (certificate, _, _) =>
                            SensorIdentityValidator.IsTransportTrusted(
                                certificate,
                                trustedRoot
                            );
                    })
            );
        });
    }
}

/// <summary>
/// Binds a presented device certificate to the equipment ID it claims.
/// </summary>
/// <remarks>
/// The TLS handshake already required a certificate. This decides whether that certificate may
/// speak for this reading. Failures map to 403 rather than 400, because the reading may be
/// well-formed while the sender is not its device.
/// </remarks>
public static class SensorIdentityValidator
{
    public static bool IsTransportTrusted(
        X509Certificate2 certificate,
        X509Certificate2 trustedRoot
    ) =>
        CertificateTrust.IsTrustedFor(
            certificate,
            trustedRoot,
            CertificateTrust.ClientAuthenticationOid
        );

    public static string? Validate(
        X509Certificate2? certificate,
        X509Certificate2? trustedRoot,
        string equipmentId
    )
    {
        if (certificate is null)
            return "a sensor client certificate is required";

        // Compare names first. A compromised device from another shard fails here without the
        // cost of a chain build.
        var subjectName = certificate.GetNameInfo(X509NameType.SimpleName, false);
        if (!string.Equals(subjectName, equipmentId, StringComparison.Ordinal))
            return $"certificate {subjectName} is not authorized for {equipmentId}";

        if (
            trustedRoot is null
            || !CertificateTrust.IsTrustedFor(
                certificate,
                trustedRoot,
                CertificateTrust.ClientAuthenticationOid
            )
        )
            return "certificate is not issued by the trusted sensor CA";

        return null;
    }
}
