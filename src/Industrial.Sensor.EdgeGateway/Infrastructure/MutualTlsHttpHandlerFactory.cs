using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

using Industrial.Sensor.EdgeGateway.Configuration;
using Industrial.Shared;

namespace Industrial.Sensor.EdgeGateway.Infrastructure;

public static class MutualTlsHttpHandlerFactory
{
    public static HttpMessageHandler Create(TransportSecurityOptions options)
    {
        var clientCertificate = X509CertificateLoader.LoadPkcs12FromFile(
            options.ClientCertificatePath,
            options.ClientCertificatePassword,
            X509KeyStorageFlags.EphemeralKeySet
        );
        try
        {
            var trustedRoot = X509CertificateLoader.LoadCertificateFromFile(
                options.TrustedServerCaPath
            );
            return new OwnedCertificateHttpClientHandler(clientCertificate, trustedRoot);
        }
        catch
        {
            clientCertificate.Dispose();
            throw;
        }
    }

    private sealed class OwnedCertificateHttpClientHandler : HttpClientHandler
    {
        private readonly X509Certificate2 _clientCertificate;
        private readonly X509Certificate2 _trustedRoot;

        public OwnedCertificateHttpClientHandler(
            X509Certificate2 clientCertificate,
            X509Certificate2 trustedRoot
        )
        {
            _clientCertificate = clientCertificate;
            _trustedRoot = trustedRoot;
            ClientCertificates.Add(clientCertificate);
            ServerCertificateCustomValidationCallback = ValidateServer;
        }

        private bool ValidateServer(
            HttpRequestMessage _,
            X509Certificate2? certificate,
            X509Chain? __,
            SslPolicyErrors errors
        ) =>
            certificate is not null
            && !errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable)
            && !errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)
            && CertificateTrust.IsTrustedFor(
                certificate,
                _trustedRoot,
                CertificateTrust.ServerAuthenticationOid
            );

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _clientCertificate.Dispose();
                _trustedRoot.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
