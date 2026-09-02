namespace Industrial.Sensor.EdgeGateway.Features.Buffer;

/// <summary>
/// Serializes the two transitions that move a MessageId between durable gateway states.
/// </summary>
/// <remarks>
/// SQLite already serializes writers, but the idempotency decision spans two tables: a live
/// reading and a retained completion marker. Holding this gate across the check and commit keeps
/// a late sensor retry from slipping between the uploader's delete and marker insert.
/// </remarks>
public sealed class BufferMutationGate : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public Task EnterAsync(CancellationToken cancellationToken = default) =>
        _semaphore.WaitAsync(cancellationToken);

    public void Exit() => _semaphore.Release();

    public void Dispose() => _semaphore.Dispose();
}
