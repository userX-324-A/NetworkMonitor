using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkMonitorService.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDiskIOColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
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

            migrationBuilder.CreateTable(
                name: "DiskActivityLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Timestamp = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ProcessId = table.Column<int>(type: "int", nullable: false),
                    ProcessName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    BytesRead = table.Column<long>(type: "bigint", nullable: false),
                    BytesWritten = table.Column<long>(type: "bigint", nullable: false),
                    ReadOperationCount = table.Column<long>(type: "bigint", nullable: false),
                    WriteOperationCount = table.Column<long>(type: "bigint", nullable: false),
                    TopReadFile = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TopWriteFile = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TopOtherFile = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OtherOperationCount = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiskActivityLogs", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DiskActivityLogs");

            migrationBuilder.DropColumn(
                name: "BytesRead",
                table: "NetworkUsageLogs");

            migrationBuilder.DropColumn(
                name: "BytesWritten",
                table: "NetworkUsageLogs");
        }
    }
}
