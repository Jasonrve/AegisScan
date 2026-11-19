using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScan.KubeOps.Data.Migrations
{
    /// <inheritdoc />
    public partial class ForgeLinksAndPreviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Preview",
                table: "Findings",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "PullRequestNumber",
                table: "Findings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CommitSHA",
                table: "Executions",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "LinksJson",
                table: "Executions",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Preview",
                table: "Findings");

            migrationBuilder.DropColumn(
                name: "PullRequestNumber",
                table: "Findings");

            migrationBuilder.DropColumn(
                name: "CommitSHA",
                table: "Executions");

            migrationBuilder.DropColumn(
                name: "LinksJson",
                table: "Executions");
        }
    }
}
