using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkMonitorService.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveDiskColumnsFromNetworkUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BytesRead",
                table: "NetworkUsageLogs");

            migrationBuilder.DropColumn(
                name: "BytesWritten",
                table: "NetworkUsageLogs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "BytesRead",
                table: "NetworkUsageLogs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "BytesWritten",
                table: "NetworkUsageLogs",
                type: "bigint",
                nullable: true);
        }
    }
}
