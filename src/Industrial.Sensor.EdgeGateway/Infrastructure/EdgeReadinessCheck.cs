using System.Data.Common;

using Industrial.Sensor.EdgeGateway.Features.Buffer;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Industrial.Sensor.EdgeGateway.Infrastructure;

/// <summary>Proves the local SQLite queue can acquire and release a write transaction.</summary>
public sealed class EdgeReadinessCheck(IServiceScopeFactory scopeFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<EdgeDbContext>();
            var connection = db.Database.GetDbConnection();

            await db.Database.OpenConnectionAsync(cancellationToken);
            try
            {
                // CanConnect only proves reads. BEGIN IMMEDIATE takes SQLite's write reservation
                // without changing data, so a read-only mount or unusable disk reports unready.
                await using DbCommand command = connection.CreateCommand();
                command.CommandText = "BEGIN IMMEDIATE; ROLLBACK;";
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }

            return HealthCheckResult.Healthy("SQLite queue is writable.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("SQLite queue is not writable.", exception);
        }
    }
}
