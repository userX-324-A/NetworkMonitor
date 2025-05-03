using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkMonitorService.Data.Migrations
{
    /// <inheritdoc />
    public partial class SomeChange : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "WriteOperationCount",
                table: "DiskActivityLogs",
                newName: "WriteOperations");

            migrationBuilder.RenameColumn(
                name: "ReadOperationCount",
                table: "DiskActivityLogs",
                newName: "ReadOperations");

            migrationBuilder.AlterColumn<string>(
                name: "ProcessName",
                table: "NetworkUsageLogs",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ProcessName",
                table: "DiskActivityLogs",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "WriteOperations",
                table: "DiskActivityLogs",
                newName: "WriteOperationCount");

            migrationBuilder.RenameColumn(
                name: "ReadOperations",
                table: "DiskActivityLogs",
                newName: "ReadOperationCount");

            migrationBuilder.AlterColumn<string>(
                name: "ProcessName",
                table: "NetworkUsageLogs",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AlterColumn<string>(
                name: "ProcessName",
                table: "DiskActivityLogs",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");
        }
    }
}
