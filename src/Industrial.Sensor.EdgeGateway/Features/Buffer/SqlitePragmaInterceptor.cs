using System.Data.Common;

using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Industrial.Sensor.EdgeGateway.Features.Buffer;

/// <summary>
/// Applies durability PRAGMAs to every pooled SQLite connection.
/// </summary>
/// <remarks>
/// journal_mode=WAL persists in the database file header, so startup sets it once.
/// synchronous is a per-connection setting and EF pools connections, so this interceptor applies
/// it on every open.
///
/// NORMAL survives a process kill but not power loss. Edge hardware is likely to lose power, so
/// the gateway uses FULL and pays an fsync per commit.
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
        // busy_timeout makes a concurrent reader or writer wait for the lock instead of failing.
        cmd.CommandText = "PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
    }
}
