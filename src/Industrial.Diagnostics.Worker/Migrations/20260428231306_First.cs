using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

// up -> how to create the table
// down -> how to undo the changes
// <inheritdoc /> makes it inherit the docs from the parent
namespace Industrial.Diagnostics.Worker.Migrations;

/// <inheritdoc />
public partial class First : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "TelemetryReadings",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                EquipmentId = table.Column<string>(type: "text", nullable: false),
                Timestamp = table.Column<DateTime>(
                    type: "timestamp with time zone",
                    nullable: false
                ),
                EngineTemperature = table.Column<double>(type: "double precision", nullable: false),
                OilPressure = table.Column<double>(type: "double precision", nullable: false),
                IsAnomaly = table.Column<bool>(type: "boolean", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TelemetryReadings", x => x.Id);
            }
        );
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "TelemetryReadings");
    }
}
