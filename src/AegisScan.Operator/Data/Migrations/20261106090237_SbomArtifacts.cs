using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScan.KubeOps.Data.Migrations
{
    /// <inheritdoc />
    public partial class SbomArtifacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RepositoryImages",
                columns: table => new
                {
                    RepositoryId = table.Column<string>(type: "text", nullable: false),
                    Ref = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepositoryImages", x => new { x.RepositoryId, x.Ref });
                });

            migrationBuilder.CreateTable(
                name: "Sboms",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    RepositoryId = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Ref = table.Column<string>(type: "text", nullable: false),
                    Origin = table.Column<string>(type: "text", nullable: false),
                    Source = table.Column<string>(type: "text", nullable: false),
                    CommitSHA = table.Column<string>(type: "text", nullable: false),
                    ExecutionId = table.Column<string>(type: "text", nullable: true),
                    GeneratedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ComponentCount = table.Column<int>(type: "integer", nullable: false),
                    Critical = table.Column<int>(type: "integer", nullable: false),
                    High = table.Column<int>(type: "integer", nullable: false),
                    Medium = table.Column<int>(type: "integer", nullable: false),
                    Low = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "text", nullable: false),
                    Document = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sboms", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "Scanners",
                columns: new[] { "Id", "ArgsJson", "BuiltIn", "Category", "CommandJson", "Enabled", "Image", "Name", "OutputFormat", "SuccessExitCodesJson" },
                values: new object[] { "sbom", "[]", true, "vulnerabilities", "[]", true, "aquasec/trivy:0.58.1", "SBOM (Trivy)", "native", "[0]" });

            migrationBuilder.CreateIndex(
                name: "IX_Sboms_RepositoryId",
                table: "Sboms",
                column: "RepositoryId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RepositoryImages");

            migrationBuilder.DropTable(
                name: "Sboms");

            migrationBuilder.DeleteData(
                table: "Scanners",
                keyColumn: "Id",
                keyValue: "sbom");
        }
    }
}
