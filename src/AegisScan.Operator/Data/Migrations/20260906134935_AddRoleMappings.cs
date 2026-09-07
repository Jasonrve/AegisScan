using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScan.KubeOps.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRoleMappings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RoleMappings",
                columns: table => new
                {
                    IdpGroup = table.Column<string>(type: "text", nullable: false),
                    AegisRole = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleMappings", x => x.IdpGroup);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RoleMappings");
        }
    }
}
