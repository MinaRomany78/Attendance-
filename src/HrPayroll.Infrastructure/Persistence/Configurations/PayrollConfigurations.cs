using HrPayroll.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HrPayroll.Infrastructure.Persistence.Configurations;

public class LoanConfiguration : IEntityTypeConfiguration<Loan>
{
    public void Configure(EntityTypeBuilder<Loan> b)
    {
        b.ToTable("Loans");
        b.Property(x => x.Amount).HasColumnType("decimal(18,2)");
        b.Property(x => x.InstallmentAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.PaidAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.OutstandingBalance).HasColumnType("decimal(18,2)");
        b.Property(x => x.Notes).HasMaxLength(1000);

        b.HasOne(x => x.Employee).WithMany(e => e.Loans)
            .HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.PayrollItem).WithMany(p => p.Loans)
            .HasForeignKey(x => x.PayrollItemId).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => new { x.EmployeeId, x.DeductionYear, x.DeductionMonth });
        b.HasIndex(x => x.PayrollItemId);
    }
}
