using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Industrial.Ingestion.Api.Infrastructure;

/// <summary>
/// Validates gateway certificates against a periodically reloaded trust snapshot.
/// Removing a fingerprint from the allowlist file revokes that gateway within one
/// reload interval, without restarting ingestion. A reload that fails (missing or
/// malformed file) keeps the previous snapshot and logs, so a bad edit fails
/// closed to the last good policy instead of locking every gateway out at once.
/// </summary>
/// <remarks>
/// Snapshots are immutable and swapped atomically, so handshake threads never see
/// a half-loaded policy. The two retried generations bound native handle lifetime:
/// the snapshot displaced two swaps ago is disposed, while in-flight handshakes
/// can still reference at most the current and previous roots.
/// </remarks>
public sealed class ReloadingClientCertificatePolicy : IDisposable
{
    private readonly string _rootPath;
    private readonly string _allowlistPath;
    private readonly ILogger<ReloadingClientCertificatePolicy> _logger;
    private readonly Timer _timer;
    private readonly object _reloadLock = new();

    private volatile ClientCertificatePolicy _current;
    private ClientCertificatePolicy? _previous;
    private byte[] _lastRootContent;
    private string _lastAllowlistContent;
    private bool _disposed;

    public ReloadingClientCertificatePolicy(
        ClientCertificatePolicy initial,
        string rootPath,
        string allowlistPath,
        TimeSpan reloadInterval,
        ILogger<ReloadingClientCertificatePolicy> logger
    )
    {
        _current = initial;
        _rootPath = rootPath;
        _allowlistPath = allowlistPath;
        _logger = logger;
        _lastRootContent = ReadRootContent();
        _lastAllowlistContent = ReadAllowlistContent();
        _timer = new Timer(
            static state => ((ReloadingClientCertificatePolicy)state!).ReloadSafely(),
            this,
            reloadInterval,
            reloadInterval
        );
    }

    public bool IsAllowed(X509Certificate2 certificate) => _current.IsAllowed(certificate);

    internal void ReloadIfChanged()
    {
        byte[] rootContent;
        string allowlistContent;

        try
        {
            rootContent = ReadRootContent();
            allowlistContent = ReadAllowlistContent();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                exception,
                "Gateway trust files are unreadable; keeping the previous certificate policy."
            );
            return;
        }

        if (
            rootContent.SequenceEqual(_lastRootContent)
            && string.Equals(allowlistContent, _lastAllowlistContent, StringComparison.Ordinal)
        )
        {
            return;
        }

        ClientCertificatePolicy reloaded;

        try
        {
            reloaded = ClientCertificatePolicy.Load(_rootPath, _allowlistPath);
        }
        catch (Exception exception)
            when (exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or CryptographicException)
        {
            // A half-written or otherwise invalid edit must not revoke every gateway.
            _logger.LogWarning(
                exception,
                "Gateway trust reload failed; keeping the previous certificate policy."
            );
            return;
        }

        lock (_reloadLock)
        {
            var retired = _current;
            _previous?.Dispose();
            _previous = retired;
            _current = reloaded;
            _lastRootContent = rootContent;
            _lastAllowlistContent = allowlistContent;
        }

        _logger.LogInformation("Gateway certificate policy reloaded from disk.");
    }

    private void ReloadSafely()
    {
        try
        {
            ReloadIfChanged();
        }
        catch (Exception exception)
        {
            // A timer callback must never take the process down with it.
            _logger.LogError(exception, "Gateway trust reload unexpectedly failed.");
        }
    }

    private byte[] ReadRootContent() => File.ReadAllBytes(_rootPath);

    private string ReadAllowlistContent() => File.ReadAllText(_allowlistPath);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _timer.Dispose();
        lock (_reloadLock)
        {
            _previous?.Dispose();
            _current.Dispose();
        }
    }
}
