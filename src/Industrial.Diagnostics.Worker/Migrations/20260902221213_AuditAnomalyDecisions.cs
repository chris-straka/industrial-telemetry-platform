using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Industrial.Diagnostics.Worker.Migrations
{
    /// <inheritdoc />
    public partial class AuditAnomalyDecisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DetectorHistoryCount",
                table: "TelemetryReadings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "DetectorPValue",
                table: "TelemetryReadings",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "DetectorScore",
                table: "TelemetryReadings",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DetectorVersion",
                table: "TelemetryReadings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DetectorHistoryCount",
                table: "TelemetryReadings");

            migrationBuilder.DropColumn(
                name: "DetectorPValue",
                table: "TelemetryReadings");

            migrationBuilder.DropColumn(
                name: "DetectorScore",
                table: "TelemetryReadings");

            migrationBuilder.DropColumn(
                name: "DetectorVersion",
                table: "TelemetryReadings");
        }
    }
}
