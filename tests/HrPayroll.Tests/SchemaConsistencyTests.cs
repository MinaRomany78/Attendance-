using HrPayroll.Domain.Entities;
using HrPayroll.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace HrPayroll.Tests;

/// <summary>
/// حماية دائمة من الأخطاء التي ظهرت فعليًا على قاعدة البيانات:
/// "Invalid object name 'SalaryAdvances'" و"Invalid column name 'InstallmentAmount'".
/// القاعدة: كل جدول وكل عمود في موديل EF يجب أن يكون ناتجًا عن ترحيل موجود،
/// وإلا فالتطبيق سيفشل على قاعدة بيانات حقيقية رغم نجاح الـ Build.
/// </summary>
public class SchemaConsistencyTests
{
    [Fact]
    public void Every_Entity_Table_And_Column_Is_Created_By_A_Migration()
    {
        using var db = new TestDb();
        using var ctx = db.CreateContext();

        var migrationTypes = ctx.GetService<IMigrationsAssembly>().Migrations.Values.ToList();
        Assert.NotEmpty(migrationTypes);

        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var typeInfo in migrationTypes)
        {
            var migration = Assert.IsAssignableFrom<Migration>(Activator.CreateInstance(typeInfo.AsType()));
            foreach (var op in migration.UpOperations)
            {
                switch (op)
                {
                    case CreateTableOperation create:
                        tables.Add(create.Name);
                        foreach (var column in create.Columns)
                            if (column.Name is not null)
                                columns.Add($"{create.Name}.{column.Name}");
                        break;
                    case AddColumnOperation add:
                        if (add.Table is not null && add.Name is not null)
                            columns.Add($"{add.Table}.{add.Name}");
                        break;
                    case RenameColumnOperation rename:
                        columns.Remove($"{rename.Table}.{rename.Name}");
                        columns.Add($"{rename.Table}.{rename.NewName}");
                        break;
                    case RenameTableOperation renameTable:
                        tables.Remove(renameTable.Name);
                        if (renameTable.NewName is not null)
                            tables.Add(renameTable.NewName);
                        break;
                }
            }
        }

        var problems = new List<string>();

        foreach (var entity in ctx.Model.GetEntityTypes().Where(e => e.GetTableName() is not null))
        {
            if (entity.IsOwned()) continue;

            var table = entity.GetTableName()!;
            if (!tables.Contains(table))
            {
                problems.Add($"جدول مفقود في الترحيلات: {table}");
                continue;
            }

            foreach (var property in entity.GetProperties())
            {
                var column = property.GetColumnName();
                if (!columns.Contains($"{table}.{column}"))
                    problems.Add($"عمود مفقود في الترحيلات: {table}.{column} ({property.Name})");
            }
        }

        Assert.True(problems.Count == 0,
            "موديل EF يحتوي على جداول/أعمدة غير موجودة في أي ترحيل — أضف ترحيلًا قبل النشر:" +
            Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void SalaryAdvances_Table_And_Loan_Repayment_Columns_Are_Mapped()
    {
        using var db = new TestDb();
        using var ctx = db.CreateContext();

        // الجدول الذي كان يسبب Invalid object name 'SalaryAdvances'
        var advance = ctx.Model.FindEntityType(typeof(SalaryAdvance));
        Assert.NotNull(advance);
        Assert.Equal("SalaryAdvances", advance!.GetTableName());

        // الأعمدة التي كانت تسبب Invalid column name على جدول Loans
        var loan = ctx.Model.FindEntityType(typeof(Loan));
        Assert.NotNull(loan);
        foreach (var name in new[]
                 {
                     "InstallmentAmount", "IsPaused", "OutstandingBalance", "PaidAmount",
                     "RepaymentStartMonth", "RepaymentStartYear"
                 })
        {
            var property = loan!.FindProperty(name);
            Assert.NotNull(property);
            // المبالغ المالية يجب أن تكون decimal بدرجة دقة 18,2 في SQL Server
            if (name is "InstallmentAmount" or "OutstandingBalance" or "PaidAmount")
                Assert.Equal("decimal(18,2)", property!.GetColumnType());
        }

        var repayment = ctx.Model.FindEntityType(typeof(LoanRepayment));
        Assert.NotNull(repayment);
        Assert.Equal("LoanRepayments", repayment!.GetTableName());

        var expense = ctx.Model.FindEntityType(typeof(EmployeeExpense));
        Assert.NotNull(expense);
        Assert.Equal("EmployeeExpenses", expense!.GetTableName());
    }
}
