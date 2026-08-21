using Industrial.Diagnostics.Worker.Features.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace Industrial.Diagnostics.Worker.Infrastructure.Data;

// This is where we define the SQL table/schema
// relationships (many/one-to-many), constraints, indexes, precision...
// It's registered as "Scoped" i.e. temporary
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options) { }

    // Set creates an SQL table with TelemetryReading for its fields
    // Set is NOT a mathematical set
    public DbSet<TelemetryReading> TelemetryReadings => Set<TelemetryReading>();
}
