using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Industrial.Diagnostics.Worker.Migrations;

/// <inheritdoc />
public partial class RecordDetectionMethod : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "DetectionMethod",
            table: "TelemetryReadings",
            type: "character varying(32)",
            maxLength: 32,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "DetectionMethod",
            table: "TelemetryReadings");
    }
}
