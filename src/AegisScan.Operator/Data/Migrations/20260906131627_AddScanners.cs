using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace AegisScan.KubeOps.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddScanners : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Scanners",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Image = table.Column<string>(type: "text", nullable: false),
                    CommandJson = table.Column<string>(type: "text", nullable: false),
                    ArgsJson = table.Column<string>(type: "text", nullable: false),
                    BuiltIn = table.Column<bool>(type: "boolean", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Scanners", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "Scanners",
                columns: new[] { "Id", "ArgsJson", "BuiltIn", "CommandJson", "Enabled", "Image", "Name" },
                values: new object[,]
                {
                    { "renovate", "[]", true, "[]", true, "renovate/renovate:latest", "Renovate" },
                    { "trufflehog", "[]", true, "[\"trufflehog\",\"filesystem\",\"{{workspace}}\",\"--json\",\"--no-update\"]", true, "trufflesecurity/trufflehog:3.90.8", "TruffleHog" }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Scanners");
        }
    }
}
