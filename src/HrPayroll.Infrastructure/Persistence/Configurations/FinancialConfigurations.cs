using HrPayroll.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HrPayroll.Infrastructure.Persistence.Configurations;

public class EmployeeExpenseConfiguration : IEntityTypeConfiguration<EmployeeExpense>
{
    public void Configure(EntityTypeBuilder<EmployeeExpense> b)
    {
        b.ToTable("EmployeeExpenses");
        b.Property(x => x.Amount).HasColumnType("decimal(18,2)");
        b.Property(x => x.Category).IsRequired().HasMaxLength(150);
        b.Property(x => x.Description).HasMaxLength(1000);
        b.Property(x => x.ReceiptReference).HasMaxLength(200);
        b.Property(x => x.Currency).IsRequired().HasMaxLength(10);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.ApprovedBy).HasMaxLength(200);

        b.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.SalaryAdvance).WithMany(a => a.LinkedExpenses)
            .HasForeignKey(x => x.SalaryAdvanceId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne(x => x.PayrollItem).WithMany().HasForeignKey(x => x.PayrollItemId).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => new { x.EmployeeId, x.Year, x.Month });
        b.HasIndex(x => x.PayrollItemId);
        b.HasIndex(x => x.Status);
    }
}

public class SalaryAdvanceConfiguration : IEntityTypeConfiguration<SalaryAdvance>
{
    public void Configure(EntityTypeBuilder<SalaryAdvance> b)
    {
        b.ToTable("SalaryAdvances");
        b.Property(x => x.BasicSalary).HasColumnType("decimal(18,2)");
        b.Property(x => x.EarnedAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.WorkingHours).HasColumnType("decimal(18,2)");
        b.Property(x => x.LateDeduction).HasColumnType("decimal(18,2)");
        b.Property(x => x.AbsenceDays).HasColumnType("decimal(6,2)");
        b.Property(x => x.AbsenceDeduction).HasColumnType("decimal(18,2)");
        b.Property(x => x.OvertimeAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.Bonuses).HasColumnType("decimal(18,2)");
        b.Property(x => x.IncludedExpenses).HasColumnType("decimal(18,2)");
        b.Property(x => x.ProposedAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.ApprovedAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.PaidAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.AdjustmentReason).HasMaxLength(1000);
        b.Property(x => x.ApprovedBy).HasMaxLength(200);
        b.Property(x => x.PaidBy).HasMaxLength(200);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.CutoffDate).HasColumnType("datetime2");

        b.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.PayrollItem).WithMany().HasForeignKey(x => x.PayrollItemId).OnDelete(DeleteBehavior.Restrict);

        // منع سلفة مكررة لنفس الموظف والفترة (ما لم تُلغَ).
        b.HasIndex(x => new { x.EmployeeId, x.Year, x.Month }).IsUnique();
        b.HasIndex(x => x.PayrollItemId);
        b.HasIndex(x => x.Status);
    }
}

public class LoanRepaymentConfiguration : IEntityTypeConfiguration<LoanRepayment>
{
    public void Configure(EntityTypeBuilder<LoanRepayment> b)
    {
        b.ToTable("LoanRepayments");
        b.Property(x => x.DueAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.PaidAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.Notes).HasMaxLength(1000);

        b.HasOne(x => x.Loan).WithMany(l => l.Repayments)
            .HasForeignKey(x => x.LoanId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne(x => x.PayrollItem).WithMany().HasForeignKey(x => x.PayrollItemId).OnDelete(DeleteBehavior.Restrict);

        // قسط واحد فقط لكل (قرض × شهر استحقاق) — يمنع الخصم المزدوج.
        b.HasIndex(x => new { x.LoanId, x.Year, x.Month }).IsUnique();
        b.HasIndex(x => x.PayrollItemId);
        b.HasIndex(x => new { x.EmployeeId, x.Year, x.Month });
    }
}
