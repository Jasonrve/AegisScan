using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScan.KubeOps.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRepositoryLanguages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LanguagesJson",
                table: "Repositories",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LanguagesJson",
                table: "Repositories");
        }
    }
}
