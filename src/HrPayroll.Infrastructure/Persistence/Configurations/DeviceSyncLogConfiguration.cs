using HrPayroll.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace HrPayroll.Infrastructure.Persistence.Configurations;
public sealed class DeviceSyncLogConfiguration : IEntityTypeConfiguration<DeviceSyncLog>
{
    public void Configure(EntityTypeBuilder<DeviceSyncLog> b)
    {
        b.HasOne(l => l.AttendanceDevice).WithMany().HasForeignKey(l => l.AttendanceDeviceId).OnDelete(DeleteBehavior.Restrict);
        b.Property(l => l.Error).HasMaxLength(2000);
        b.HasIndex(l => new { l.AttendanceDeviceId, l.CreatedAt });
    }
}
