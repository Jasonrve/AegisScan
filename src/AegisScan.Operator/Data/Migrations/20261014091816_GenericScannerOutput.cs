using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScan.KubeOps.Data.Migrations
{
    /// <inheritdoc />
    public partial class GenericScannerOutput : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Category",
                table: "Scanners",
                type: "text",
                nullable: false,
                defaultValue: "other");

            migrationBuilder.AddColumn<string>(
                name: "OutputFormat",
                table: "Scanners",
                type: "text",
                nullable: false,
                defaultValue: "none");

            migrationBuilder.AddColumn<string>(
                name: "SuccessExitCodesJson",
                table: "Scanners",
                type: "text",
                nullable: false,
                defaultValue: "[0]");

            migrationBuilder.AddColumn<string>(
                name: "Category",
                table: "Findings",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "HelpUrl",
                table: "Findings",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Severity",
                table: "Findings",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "OutputInterpreted",
                table: "Executions",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.Sql("UPDATE \"Findings\" SET \"Category\" = CASE \"Scanner\" WHEN 'trufflehog' THEN 'secrets' WHEN 'renovate' THEN 'dependencies' ELSE 'other' END;");

            migrationBuilder.UpdateData(
                table: "Scanners",
                keyColumn: "Id",
                keyValue: "renovate",
                columns: new[] { "Category", "OutputFormat", "SuccessExitCodesJson" },
                values: new object[] { "dependencies", "native", "[0]" });

            migrationBuilder.UpdateData(
                table: "Scanners",
                keyColumn: "Id",
                keyValue: "trufflehog",
                columns: new[] { "Category", "OutputFormat", "SuccessExitCodesJson" },
                values: new object[] { "secrets", "native", "[0]" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Category",
                table: "Scanners");

            migrationBuilder.DropColumn(
                name: "OutputFormat",
                table: "Scanners");

            migrationBuilder.DropColumn(
                name: "SuccessExitCodesJson",
                table: "Scanners");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "Findings");

            migrationBuilder.DropColumn(
                name: "HelpUrl",
                table: "Findings");

            migrationBuilder.DropColumn(
                name: "Severity",
                table: "Findings");

            migrationBuilder.DropColumn(
                name: "OutputInterpreted",
                table: "Executions");
        }
    }
}
