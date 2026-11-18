using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HrPayroll.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PayrollAccuracyAndImmediateAdvances : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "RegisteredAmount",
                table: "SalaryAdvances",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AttendanceMissingDays",
                table: "PayrollItems",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "EarlyLeaveDeduction",
                table: "PayrollItems",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EarlyLeaveMinutes",
                table: "PayrollItems",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "EarnedBasicSalary",
                table: "PayrollItems",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "EarnedDeductions",
                table: "PayrollItems",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "EarnedGrossSalary",
                table: "PayrollItems",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "EarnedNetSalary",
                table: "PayrollItems",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EarningsAsOf",
                table: "PayrollItems",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ShortageDeduction",
                table: "PayrollItems",
                type: "decimal(18,2)",
                nullable: true);

            // Explicit requested policy: minute-based early-leave deduction, existing exit grace.
            // Preserve an administrator's existing choice; never rewrite historical payroll amounts.
            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM [AppSettings] WHERE [Key] = N'Payroll.EarlyLeaveDeductionEnabled')
                    INSERT INTO [AppSettings] ([Key], [Value], [Description], [CreatedAt], [CreatedBy])
                    VALUES (N'Payroll.EarlyLeaveDeductionEnabled', N'true',
                        N'خصم الانصراف المبكر بسعر الدقيقة بعد سماح الخروج، دون تقريب جديد أو خصم مزدوج',
                        SYSUTCDATETIME(), N'migration');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Financial snapshots and advance registrations must be preserved. Use a reviewed forward migration.");
        }
    }
}
