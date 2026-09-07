using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScan.KubeOps.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionJobName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "JobName",
                table: "Executions",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "JobName",
                table: "Executions");
        }
    }
}
