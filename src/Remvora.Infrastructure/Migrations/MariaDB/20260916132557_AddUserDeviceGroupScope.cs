using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Remvora.Infrastructure.Migrations.MariaDB
{
    /// <inheritdoc />
    public partial class AddUserDeviceGroupScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeviceGroupScope",
                table: "Users",
                type: "varchar(4096)",
                maxLength: 4096,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeviceGroupScope",
                table: "Users");
        }
    }
}
