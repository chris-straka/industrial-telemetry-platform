using Industrial.Diagnostics.Worker.Features.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace Industrial.Diagnostics.Worker.Infrastructure.Data;

// Creates the TelemetryReading table in my DB
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<TelemetryReading> TelemetryReadings => Set<TelemetryReading>();
}
