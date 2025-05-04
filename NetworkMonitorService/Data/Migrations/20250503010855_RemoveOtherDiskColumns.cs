using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkMonitorService.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveOtherDiskColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OtherOperationCount",
                table: "DiskActivityLogs");

            migrationBuilder.DropColumn(
                name: "TopOtherFile",
                table: "DiskActivityLogs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "OtherOperationCount",
                table: "DiskActivityLogs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "TopOtherFile",
                table: "DiskActivityLogs",
                type: "nvarchar(max)",
                nullable: true);
        }
    }
}
