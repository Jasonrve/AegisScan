using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScan.KubeOps.Data.Migrations
{
    /// <inheritdoc />
    public partial class UpdateRenovateScannerArgs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "Scanners",
                keyColumn: "Id",
                keyValue: "renovate",
                column: "ArgsJson",
                value: "[\"--platform=local\",\"--report-type=logging\"]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "Scanners",
                keyColumn: "Id",
                keyValue: "renovate",
                column: "ArgsJson",
                value: "[]");
        }
    }
}
