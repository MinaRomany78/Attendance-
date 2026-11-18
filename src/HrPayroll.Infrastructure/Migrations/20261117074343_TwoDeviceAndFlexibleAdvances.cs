using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HrPayroll.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TwoDeviceAndFlexibleAdvances : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SalaryAdvances_EmployeeId_Year_Month",
                table: "SalaryAdvances");

            migrationBuilder.AddColumn<Guid>(
                name: "PaymentRequestId",
                table: "SalaryAdvances",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedAttType",
                table: "RawPunches",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "EmployeeExpenseRecoveries",
                table: "PayrollItems",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "LegacyLoanDeductions",
                table: "PayrollItems",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "LoanInstallments",
                table: "PayrollItems",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SalaryAdvancesPaid",
                table: "PayrollItems",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConnectionStatus",
                table: "AttendanceDevices",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "LastDownloadedCount",
                table: "AttendanceDevices",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "LastSyncError",
                table: "AttendanceDevices",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Role",
                table: "AttendanceDevices",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "DeviceEmployeeMapping",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AttendanceDeviceId = table.Column<int>(type: "int", nullable: false),
                    BiometricCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    EmployeeId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceEmployeeMapping", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceEmployeeMapping_AttendanceDevices_AttendanceDeviceId",
                        column: x => x.AttendanceDeviceId,
                        principalTable: "AttendanceDevices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DeviceEmployeeMapping_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DeviceSyncLog",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AttendanceDeviceId = table.Column<int>(type: "int", nullable: false),
                    Successful = table.Column<bool>(type: "bit", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ReviewRecordsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Downloaded = table.Column<int>(type: "int", nullable: false),
                    Inserted = table.Column<int>(type: "int", nullable: false),
                    Duplicates = table.Column<int>(type: "int", nullable: false),
                    Invalid = table.Column<int>(type: "int", nullable: false),
                    Unmatched = table.Column<int>(type: "int", nullable: false),
                    Review = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceSyncLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceSyncLog_AttendanceDevices_AttendanceDeviceId",
                        column: x => x.AttendanceDeviceId,
                        principalTable: "AttendanceDevices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SalaryAdvances_EmployeeId_Year_Month",
                table: "SalaryAdvances",
                columns: new[] { "EmployeeId", "Year", "Month" });

            migrationBuilder.CreateIndex(
                name: "IX_SalaryAdvances_PaymentRequestId",
                table: "SalaryAdvances",
                column: "PaymentRequestId",
                unique: true,
                filter: "[PaymentRequestId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceEmployeeMapping_AttendanceDeviceId_BiometricCode",
                table: "DeviceEmployeeMapping",
                columns: new[] { "AttendanceDeviceId", "BiometricCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceEmployeeMapping_EmployeeId",
                table: "DeviceEmployeeMapping",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceSyncLog_AttendanceDeviceId_CreatedAt",
                table: "DeviceSyncLog",
                columns: new[] { "AttendanceDeviceId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("This additive financial migration cannot be rolled back destructively. Use a reviewed forward migration.");
        }
    }
}