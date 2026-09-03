using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Industrial.Diagnostics.Worker.Migrations;

/// <inheritdoc />
public partial class InitialCreate : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "TelemetryReadings",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                EquipmentId = table.Column<string>(type: "text", nullable: false),
                SequenceNumber = table.Column<long>(type: "bigint", nullable: false),
                OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                PersistedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                EngineTemperature = table.Column<double>(type: "double precision", nullable: false),
                OilPressure = table.Column<double>(type: "double precision", nullable: false),
                IsAnomaly = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TelemetryReadings", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_TelemetryReadings_EquipmentId_OccurredAt",
            table: "TelemetryReadings",
            columns: new[] { "EquipmentId", "OccurredAt" });

        migrationBuilder.CreateIndex(
            name: "IX_TelemetryReadings_MessageId",
            table: "TelemetryReadings",
            column: "MessageId",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "TelemetryReadings");
    }
}
