using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HrPayroll.Infrastructure.Migrations;

[Migration("20261116120000_AttendanceImportDeviceSupport")]
public partial class AttendanceImportDeviceSupport : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "OriginalFieldsJson", table: "RawPunches", type: "nvarchar(max)", nullable: true);
        migrationBuilder.AddColumn<int>(name: "ReviewRows", table: "ImportBatches", type: "int", nullable: false, defaultValue: 0);
        migrationBuilder.CreateTable(
            name: "AttendanceDevices",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false).Annotation("SqlServer:Identity", "1, 1"),
                Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                IpAddress = table.Column<string>(type: "nvarchar(45)", maxLength: 45, nullable: false),
                TcpPort = table.Column<int>(type: "int", nullable: false),
                Model = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                Identifier = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                TimeZoneId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                ProtectedCommunicationKey = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                LastSuccessfulSyncUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                UpdatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_AttendanceDevices", x => x.Id));
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Deliberately refuse automated rollback: removing imported diagnostics/configuration loses data.
        throw new NotSupportedException("راجع خطة الاحتفاظ بالبيانات يدوياً قبل التراجع عن هذا الترحيل الإضافي.");
    }
}
