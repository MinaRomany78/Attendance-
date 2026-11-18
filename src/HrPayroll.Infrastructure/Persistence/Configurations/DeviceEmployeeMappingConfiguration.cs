using HrPayroll.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace HrPayroll.Infrastructure.Persistence.Configurations;
public sealed class DeviceEmployeeMappingConfiguration : IEntityTypeConfiguration<DeviceEmployeeMapping>
{
    public void Configure(EntityTypeBuilder<DeviceEmployeeMapping> b)
    {
        b.Property(x => x.BiometricCode).HasMaxLength(50).IsRequired();
        b.HasIndex(x => new { x.AttendanceDeviceId, x.BiometricCode }).IsUnique();
        b.HasOne(x => x.AttendanceDevice).WithMany().HasForeignKey(x => x.AttendanceDeviceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
    }
}
