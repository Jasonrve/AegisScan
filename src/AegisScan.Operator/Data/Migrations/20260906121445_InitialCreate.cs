using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace AegisScan.KubeOps.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Executions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    RepositoryId = table.Column<string>(type: "text", nullable: false),
                    Scanner = table.Column<string>(type: "text", nullable: false),
                    Mode = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Attempt = table.Column<int>(type: "integer", nullable: false),
                    SanitizedLog = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Executions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Findings",
                columns: table => new
                {
                    Fingerprint = table.Column<string>(type: "text", nullable: false),
                    RepositoryId = table.Column<string>(type: "text", nullable: false),
                    Detector = table.Column<string>(type: "text", nullable: false),
                    Verified = table.Column<bool>(type: "boolean", nullable: false),
                    SafePath = table.Column<string>(type: "text", nullable: false),
                    CommitSHA = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Findings", x => x.Fingerprint);
                });

            migrationBuilder.CreateTable(
                name: "Repositories",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Provider = table.Column<string>(type: "text", nullable: false),
                    CloneUrl = table.Column<string>(type: "text", nullable: false),
                    DefaultBranch = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    CacheState = table.Column<string>(type: "text", nullable: false),
                    ActiveFindingCount = table.Column<int>(type: "integer", nullable: false),
                    LatestCommitSHA = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Repositories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.InsertData(
                table: "Repositories",
                columns: new[] { "Id", "ActiveFindingCount", "CacheState", "CloneUrl", "DefaultBranch", "LatestCommitSHA", "Name", "Provider", "Status" },
                values: new object[,]
                {
                    { "acme-web", 0, "warm", "https://github.com/acme/acme-web.git", "main", "", "acme-web", "github", "Ready" },
                    { "mobile-app", 0, "stale", "https://github.com/acme/mobile-app.git", "main", "", "mobile-app", "github", "Ready" },
                    { "platform-api", 0, "warm", "https://gitlab.com/acme/platform-api.git", "main", "", "platform-api", "gitlab", "Ready" }
                });

            migrationBuilder.InsertData(
                table: "Settings",
                columns: new[] { "Key", "Value" },
                values: new object[,]
                {
                    { "cacheRetentionGB", "20" },
                    { "retentionDays", "90" }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Executions");

            migrationBuilder.DropTable(
                name: "Findings");

            migrationBuilder.DropTable(
                name: "Repositories");

            migrationBuilder.DropTable(
                name: "Settings");
        }
    }
}
