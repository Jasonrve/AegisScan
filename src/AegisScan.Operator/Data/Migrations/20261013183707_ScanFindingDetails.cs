using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScan.KubeOps.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScanFindingDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Detail",
                table: "Findings",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ExecutionId",
                table: "Findings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FirstSeenAt",
                table: "Findings",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastSeenAt",
                table: "Findings",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<string>(
                name: "Scanner",
                table: "Findings",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "UpdateType",
                table: "Findings",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "FindingCount",
                table: "Executions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "OutputLog",
                table: "Executions",
                type: "text",
                nullable: true);

            // Backfill existing findings: scope them to a scanner (so auto-resolve of fixed findings never crosses
            // scanners), stamp the new first/last-seen columns, and drop the pod workspace prefix from display paths
            // (fingerprints are unaffected — the TruffleHog parser still hashes the original path).
            migrationBuilder.Sql("""
                UPDATE "Findings" SET
                    "Scanner" = CASE WHEN "Detector" LIKE 'Renovate%' THEN 'renovate' ELSE 'trufflehog' END,
                    "FirstSeenAt" = now(),
                    "LastSeenAt" = now(),
                    "SafePath" = regexp_replace("SafePath", '^/workspace/repo/', '');
                """);

            migrationBuilder.UpdateData(
                table: "Scanners",
                keyColumn: "Id",
                keyValue: "renovate",
                columns: new[] { "ArgsJson", "Image" },
                values: new object[] { "[\"--platform=local\",\"--report-type=logging\",\"--dry-run=lookup\"]", "renovate/renovate:44.155.7" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Detail",
                table: "Findings");

            migrationBuilder.DropColumn(
                name: "ExecutionId",
                table: "Findings");

            migrationBuilder.DropColumn(
                name: "FirstSeenAt",
                table: "Findings");

            migrationBuilder.DropColumn(
                name: "LastSeenAt",
                table: "Findings");

            migrationBuilder.DropColumn(
                name: "Scanner",
                table: "Findings");

            migrationBuilder.DropColumn(
                name: "UpdateType",
                table: "Findings");

            migrationBuilder.DropColumn(
                name: "FindingCount",
                table: "Executions");

            migrationBuilder.DropColumn(
                name: "OutputLog",
                table: "Executions");

            migrationBuilder.UpdateData(
                table: "Scanners",
                keyColumn: "Id",
                keyValue: "renovate",
                columns: new[] { "ArgsJson", "Image" },
                values: new object[] { "[\"--platform=local\",\"--report-type=logging\"]", "renovate/renovate:latest" });
        }
    }
}
