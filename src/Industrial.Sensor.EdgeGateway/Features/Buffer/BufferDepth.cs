namespace Industrial.Sensor.EdgeGateway.Features.Buffer;

/// <summary>
/// Holds the # of readings in the local buffer, cached in memory.
/// </summary>
/// <remarks>
/// SQLite stores no row count, so a COUNT(*) walks every row
/// Both hops move this by a delta instead: the receiver increments, the uploader decrements what it deleted
/// The uploader still resyncs from a real count on a schedule, because a delta-only estimate drifts whenever a process dies between the write and its adjustment
/// Counting per request instead would put that scan in front of every reading
/// </remarks>
public sealed class BufferDepth
{
    private long _current;

    public long Current => Interlocked.Read(ref _current);

    public void Increment() => Interlocked.Increment(ref _current);

    /// <summary>Drops the estimate by the number of rows the uploader just deleted</summary>
    /// <remarks>
    /// Pairs with Increment so the steady state never counts
    /// Interlocked.Add rather than a loop of Decrement, since the uploader deletes a whole batch at once
    /// </remarks>
    public void Decrement(long count) => Interlocked.Add(ref _current, -count);

    /// <summary>Replaces the running estimate with a real count</summary>
    /// <remarks>The periodic correction for the drift the deltas above can accumulate.</remarks>
    public void SetTo(long depth) => Interlocked.Exchange(ref _current, depth);
}
