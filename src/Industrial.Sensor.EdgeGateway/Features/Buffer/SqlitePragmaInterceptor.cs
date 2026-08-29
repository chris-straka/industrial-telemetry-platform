using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Industrial.Sensor.EdgeGateway.Features.Buffer;

/// <summary>
/// Applies durability PRAGMAs to every pooled SQLite connection.
/// </summary>
/// <remarks>
/// A pragma in SQLite is a statement that reads or sets an engine setting
/// The C# #pragma is unrelated, a compiler directive from πρᾶγμα = action
/// An interceptor is an EF hook that runs at a chosen point in the pipeline
///
/// journal_mode=WAL is PERSISTENT, written into the database file header once
/// synchronous=FULL is PER-CONNECTION, and EF pools connections
/// Setting it at startup would therefore cover only the first connection
///
/// FULL rather than synchronous=NORMAL, which survives a process kill but not power loss
/// An edge gateway is exactly the machine that loses power, so it pays an fsync per commit
/// </remarks>
public class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        Apply(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default
    )
    {
        Apply(connection);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    private static void Apply(DbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        // synchronous=FULL fsyncs the WAL before reporting a commit done
        // That makes readings survive a power loss, not just a process kill
        // busy_timeout makes a concurrent reader or writer wait instead of failing
        cmd.CommandText = "PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
    }
}
