using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HrPayroll.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PreparedMonthlyAdvanceRegister : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CycleKey",
                table: "SalaryAdvances",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PreparedAmount",
                table: "SalaryAdvances",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PreparedAt",
                table: "SalaryAdvances",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreparedBy",
                table: "SalaryAdvances",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "SalaryAdvances",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateIndex(
                name: "IX_SalaryAdvances_EmployeeId_Year_Month_CycleKey",
                table: "SalaryAdvances",
                columns: new[] { "EmployeeId", "Year", "Month", "CycleKey" },
                unique: true,
                filter: "[CycleKey] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Prepared financial registers must be preserved. Use a reviewed forward migration.");
        }
    }
}
