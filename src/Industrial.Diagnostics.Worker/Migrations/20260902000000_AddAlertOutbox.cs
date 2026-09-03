using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Industrial.Diagnostics.Worker.Migrations;

/// <inheritdoc />
public partial class AddAlertOutbox : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AlertOutboxMessages",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                EquipmentId = table.Column<string>(
                    type: "character varying(64)",
                    maxLength: 64,
                    nullable: false
                ),
                Payload = table.Column<string>(type: "text", nullable: false),
                TraceParent = table.Column<string>(
                    type: "character varying(128)",
                    maxLength: 128,
                    nullable: true
                ),
                CreatedAt = table.Column<DateTimeOffset>(
                    type: "timestamp with time zone",
                    nullable: false
                ),
                PublishedAt = table.Column<DateTimeOffset>(
                    type: "timestamp with time zone",
                    nullable: true
                ),
                AttemptCount = table.Column<int>(type: "integer", nullable: false),
                LastError = table.Column<string>(
                    type: "character varying(2000)",
                    maxLength: 2000,
                    nullable: true
                ),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AlertOutboxMessages", x => x.Id);
            }
        );

        migrationBuilder.CreateIndex(
            name: "IX_AlertOutboxMessages_MessageId",
            table: "AlertOutboxMessages",
            column: "MessageId",
            unique: true
        );

        migrationBuilder.CreateIndex(
            name: "IX_AlertOutboxMessages_PublishedAt_CreatedAt",
            table: "AlertOutboxMessages",
            columns: new[] { "PublishedAt", "CreatedAt" }
        );
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "AlertOutboxMessages");
    }
}
