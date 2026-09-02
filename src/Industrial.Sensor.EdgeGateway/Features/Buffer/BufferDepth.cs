namespace Industrial.Sensor.EdgeGateway.Features.Buffer;

/// <summary>
/// Holds the number of readings in the local buffer and reserves capacity atomically.
/// </summary>
/// <remarks>
/// SQLite stores no row count, so a COUNT(*) walks every row. Startup seeds this value from
/// disk; after that every committed insert and delete moves it by a matching delta. A compare-
/// exchange reservation makes MaxDepth a real ceiling even when several sensor requests arrive
/// together. A process crash cannot leave this stale because startup counts the durable rows again.
/// </remarks>
public sealed class BufferDepth
{
    private long _current;

    public long Current => Interlocked.Read(ref _current);

    public bool TryReserve(long maxDepth)
    {
        while (true)
        {
            var current = Interlocked.Read(ref _current);
            if (current >= maxDepth)
                return false;

            if (Interlocked.CompareExchange(ref _current, current + 1, current) == current)
                return true;
        }
    }

    /// <summary>Releases the capacity held by rows the uploader committed as deleted.</summary>
    /// <remarks>
    /// The compare-exchange rejects an impossible underflow without first corrupting the counter.
    /// </remarks>
    public void Release(long count)
    {
        if (count <= 0)
            return;

        while (true)
        {
            var current = Interlocked.Read(ref _current);
            if (count > current)
            {
                throw new InvalidOperationException(
                    "The in-memory buffer depth would fall below zero."
                );
            }

            if (Interlocked.CompareExchange(ref _current, current - count, current) == current)
                return;
        }
    }

    /// <summary>Seeds the counter from the durable row count before the server starts.</summary>
    public void Initialize(long depth)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(depth);
        Interlocked.Exchange(ref _current, depth);
    }
}
