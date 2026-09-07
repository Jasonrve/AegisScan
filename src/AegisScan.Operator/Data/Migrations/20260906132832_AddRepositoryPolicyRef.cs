using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScan.KubeOps.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRepositoryPolicyRef : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PolicyRef",
                table: "Repositories",
                type: "text",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "Repositories",
                keyColumn: "Id",
                keyValue: "acme-web",
                column: "PolicyRef",
                value: null);

            migrationBuilder.UpdateData(
                table: "Repositories",
                keyColumn: "Id",
                keyValue: "mobile-app",
                column: "PolicyRef",
                value: null);

            migrationBuilder.UpdateData(
                table: "Repositories",
                keyColumn: "Id",
                keyValue: "platform-api",
                column: "PolicyRef",
                value: null);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PolicyRef",
                table: "Repositories");
        }
    }
}
