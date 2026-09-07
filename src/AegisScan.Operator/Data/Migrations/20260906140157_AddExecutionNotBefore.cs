using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScan.KubeOps.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionNotBefore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NotBefore",
                table: "Executions",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NotBefore",
                table: "Executions");
        }
    }
}
