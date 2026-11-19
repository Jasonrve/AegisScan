using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScan.KubeOps.Data.Migrations
{
    /// <inheritdoc />
    public partial class TriageHistoryAcks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Assignee",
                table: "Findings",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "IntroducedCommit",
                table: "Findings",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ResolvedAt",
                table: "Findings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AlertAcks",
                columns: table => new
                {
                    Key = table.Column<string>(type: "text", nullable: false),
                    Actor = table.Column<string>(type: "text", nullable: false),
                    AcknowledgedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SnoozeUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertAcks", x => x.Key);
                });

            // Findings resolved before ResolvedAt existed: approximate with when they were last seen so mean-time-to-resolve has data.
            migrationBuilder.Sql("UPDATE \"Findings\" SET \"ResolvedAt\" = \"LastSeenAt\" WHERE \"State\" = 'resolved';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlertAcks");

            migrationBuilder.DropColumn(
                name: "Assignee",
                table: "Findings");

            migrationBuilder.DropColumn(
                name: "IntroducedCommit",
                table: "Findings");

            migrationBuilder.DropColumn(
                name: "ResolvedAt",
                table: "Findings");
        }
    }
}
