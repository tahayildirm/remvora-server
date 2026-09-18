using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Remvora.Infrastructure.Migrations.PostgreSQL
{
    /// <inheritdoc />
    public partial class TerminalPrivilegePolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowTerminalPrivilegeEscalation",
                table: "Devices",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowTerminalPrivilegeEscalation",
                table: "Devices");
        }
    }
}
