using HrPayroll.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HrPayroll.Infrastructure.Persistence.Configurations;

public sealed class AttendanceDeviceConfiguration : IEntityTypeConfiguration<AttendanceDevice>
{
    public void Configure(EntityTypeBuilder<AttendanceDevice> b)
    {
        b.ToTable("AttendanceDevices");
        b.Property(x => x.Name).IsRequired().HasMaxLength(150);
        b.Property(x => x.IpAddress).IsRequired().HasMaxLength(45);
        b.Property(x => x.Model).IsRequired().HasMaxLength(150);
        b.Property(x => x.Identifier).HasMaxLength(150);
        b.Property(x => x.TimeZoneId).IsRequired().HasMaxLength(100);
        b.Property(x => x.ProtectedCommunicationKey).HasMaxLength(2000);
    }
}
