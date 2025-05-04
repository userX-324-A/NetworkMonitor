using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkMonitorService.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTopRemoteIp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TopRemoteIpAddress",
                table: "NetworkUsageLogs",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TopRemoteIpAddress",
                table: "NetworkUsageLogs");
        }
    }
}
