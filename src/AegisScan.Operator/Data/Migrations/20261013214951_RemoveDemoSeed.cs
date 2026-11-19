using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace AegisScan.KubeOps.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveDemoSeed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Demo repositories are now opt-in (AEGIS_SEED_DEMO); drop their runs and findings too so nothing is orphaned.
            migrationBuilder.Sql("""
                DELETE FROM "Findings" WHERE "RepositoryId" IN ('acme-web','platform-api','mobile-app');
                DELETE FROM "Executions" WHERE "RepositoryId" IN ('acme-web','platform-api','mobile-app');
                """);

            migrationBuilder.DeleteData(
                table: "Repositories",
                keyColumn: "Id",
                keyValue: "acme-web");

            migrationBuilder.DeleteData(
                table: "Repositories",
                keyColumn: "Id",
                keyValue: "mobile-app");

            migrationBuilder.DeleteData(
                table: "Repositories",
                keyColumn: "Id",
                keyValue: "platform-api");

            migrationBuilder.DeleteData(
                table: "Settings",
                keyColumn: "Key",
                keyValue: "cacheRetentionGB");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "Repositories",
                columns: new[] { "Id", "ActiveFindingCount", "CacheState", "CloneUrl", "DefaultBranch", "LanguagesJson", "LatestCommitSHA", "Name", "PolicyRef", "Provider", "Status" },
                values: new object[,]
                {
                    { "acme-web", 0, "warm", "https://github.com/acme/acme-web.git", "main", null, "", "acme-web", null, "github", "Ready" },
                    { "mobile-app", 0, "stale", "https://github.com/acme/mobile-app.git", "main", null, "", "mobile-app", null, "github", "Ready" },
                    { "platform-api", 0, "warm", "https://gitlab.com/acme/platform-api.git", "main", null, "", "platform-api", null, "gitlab", "Ready" }
                });

            migrationBuilder.InsertData(
                table: "Settings",
                columns: new[] { "Key", "Value" },
                values: new object[] { "cacheRetentionGB", "20" });
        }
    }
}
