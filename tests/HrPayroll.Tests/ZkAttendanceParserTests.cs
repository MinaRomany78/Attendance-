using System.IO.Compression;
using System.Text;
using ClosedXML.Excel;
using HrPayroll.Infrastructure.Import;

namespace HrPayroll.Tests;

public class ZkAttendanceParserTests
{
    // Synthetic records only: no production employee data or original device logs.
    private const string Name = "موظف تجريبي";
    private static string Record(string code = "003", string direction = "I", string time = "2026-11-10 08:03:09")
        => $"{code}\t{time}\t101\t1\t{Name}\t{direction}\t0\t1";

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Headerless_Zk_Layout_Maps_Code_Time_Name_And_Direction_Without_Guessing_Metadata(string newline)
    {
        var bytes = Encoding.UTF8.GetBytes(Record() + newline + Record(direction: "O", time: "2026-11-10 08:08:24"));
        var format = AttendanceFileParser.DetectFormat(bytes);
        var sheet = AttendanceFileParser.Parse(bytes, format);
        Assert.Equal(AttendanceFileFormat.Csv, format);
        Assert.Equal(2, sheet.Rows.Count);
        Assert.Null(ColumnMapper.Detect(sheet.Rows));
        var mapping = ColumnMapper.Fallback(sheet.Rows);
        Assert.True(mapping.IsUsable);
        Assert.True(mapping.IsZkExport);
        Assert.Equal(-1, mapping.HeaderRowIndex);
        Assert.Equal(8, mapping.ExpectedColumnCount);
        Assert.Equal(0, mapping.EmployeeCode);
        Assert.Equal(1, mapping.AttTime);
        Assert.Equal(4, mapping.Name);
        Assert.Equal(5, mapping.AttType);
        Assert.Null(mapping.DeviceId);
        Assert.True(mapping.IsZkExport);
        Assert.Null(mapping.No);
        Assert.Equal("003", sheet.Rows[0][mapping.EmployeeCode!.Value]);
        Assert.Equal("I", sheet.Rows[0][mapping.AttType!.Value]);
        Assert.Equal("O", sheet.Rows[1][mapping.AttType.Value]);
    }

    [Theory]
    [InlineData("utf-8")]
    [InlineData("windows-1256")]
    public void Arabic_Names_Are_Preserved_Using_Strict_Utf8_Or_Windows1256(string encodingName)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding(encodingName).GetBytes(Record());
        var sheet = AttendanceFileParser.Parse(bytes, AttendanceFileParser.DetectFormat(bytes));
        Assert.Equal(Name, sheet.Rows.Single()[4]);
        Assert.Equal(encodingName, sheet.EncodingName);
        Assert.DoesNotContain('\uFFFD', sheet.Rows.Single()[4]);
    }

    [Fact]
    public void Utf8_Bom_And_Leading_Zero_Code_Are_Preserved()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(Record())).ToArray();
        var sheet = AttendanceFileParser.Parse(bytes, AttendanceFileParser.DetectFormat(bytes));
        Assert.Equal("003", sheet.Rows.Single()[0]);
    }

    [Fact]
    public void Names_Containing_Commas_Or_Quotes_Do_Not_Change_Tab_Delimiter_Or_Absorb_Rows()
    {
        var line = Record().Replace(Name, "موظف, \"تجريبي");
        var bytes = Encoding.UTF8.GetBytes(line + "\n" + Record(direction: "O"));
        var sheet = AttendanceFileParser.Parse(bytes, AttendanceFileParser.DetectFormat(bytes));
        Assert.Equal(2, sheet.Rows.Count);
        Assert.All(sheet.Rows, row => Assert.Equal(8, row.Length));
        Assert.Equal("O", sheet.Rows[1][5]);
    }

    [Fact]
    public void Malformed_Row_Does_Not_Hide_Later_Valid_Zk_Rows()
    {
        var bytes = Encoding.UTF8.GetBytes("003\tnot-a-date\tbroken\n" + Record() + "\n" + Record(direction: "O"));
        var sheet = AttendanceFileParser.Parse(bytes, AttendanceFileParser.DetectFormat(bytes));
        var mapping = ColumnMapper.Fallback(sheet.Rows);
        Assert.True(mapping.IsZkExport);
        Assert.Equal(3, sheet.Rows.Count);
        Assert.Equal(3, sheet.Rows[0].Length);
        Assert.Equal(-1, mapping.HeaderRowIndex);
    }

    [Fact]
    public void Header_Based_Export_Uses_Explicit_Column_Names()
    {
        var bytes = Encoding.UTF8.GetBytes("Biometric Code\tDate/Time\tMetadata A\tMetadata B\tName\tDirection\tMetadata C\tMetadata D\n" + Record());
        var sheet = AttendanceFileParser.Parse(bytes, AttendanceFileParser.DetectFormat(bytes));
        var mapping = ColumnMapper.Detect(sheet.Rows);
        Assert.NotNull(mapping);
        Assert.Equal(0, mapping.HeaderRowIndex);
        Assert.Equal(0, mapping.EmployeeCode);
        Assert.Equal(1, mapping.AttTime);
        Assert.Equal(4, mapping.Name);
        Assert.Equal(5, mapping.AttType);
        Assert.Null(mapping.DeviceId);
    }

    [Fact]
    public void Data_Values_Containing_Header_Aliases_Are_Not_Headers()
    {
        var rows = new List<string[]> { new[] { "003", "2026-11-10 08:03:09", "101", "1", "Sid Timeless", "I", "0", "1" } };
        Assert.Null(ColumnMapper.Detect(rows));
        var aliasMetadata = new List<string[]> { new[] { "003", "2026-11-10 08:03:09", "Time", "1", "ID", "I", "0", "1" } };
        Assert.Null(ColumnMapper.Detect(aliasMetadata));
        Assert.True(ColumnMapper.Fallback(aliasMetadata).IsZkExport);
    }

    [Fact]
    public void A_Header_Appearing_After_Actual_Data_Does_Not_Hide_The_First_Data_Row()
    {
        var rows = new List<string[]>
        {
            Record().Split('\t'),
            new[] { "Biometric Code", "Date/Time", "Metadata A", "Metadata B", "Name", "Direction", "Metadata C", "Metadata D" },
            Record(direction: "O").Split('\t')
        };
        var mapping = ColumnMapper.Detect(rows);
        Assert.NotNull(mapping);
        Assert.Equal(-1, mapping.HeaderRowIndex);
        Assert.True(mapping.IsZkExport);
    }

    [Fact]
    public void Headerless_Legacy_Six_Column_Layout_Retains_The_First_Record()
    {
        var rows = new List<string[]> { new[] { "1", "device-1", "003", Name, "FP", "2026-11-10 08:03:09" } };
        var mapping = ColumnMapper.Fallback(rows);
        Assert.True(mapping.IsUsable);
        Assert.False(mapping.IsZkExport);
        Assert.Equal(-1, mapping.HeaderRowIndex);
        Assert.Equal(2, mapping.EmployeeCode);
        Assert.Equal(5, mapping.AttTime);
        Assert.Equal(6, mapping.ExpectedColumnCount);
    }

    [Fact]
    public void Unknown_Layouts_Do_Not_Get_An_Arbitrary_Fallback()
    {
        Assert.False(ColumnMapper.Fallback(new List<string[]> { new[] { "a", "b", "c", "d", "e", "f" } }).IsUsable);
        Assert.Null(ZkAttendanceFormatDetector.Detect(new List<string[]> { new[] { "3", "1", "101", "1", Name, "I", "0", "1" } }));
    }

    [Fact]
    public void Format_Is_Detected_From_Excel_Content_Regardless_Of_The_File_Extension()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Attendance");
        sheet.Cell(1, 1).Value = "ID";
        sheet.Cell(1, 2).Value = "Att.Time";
        sheet.Cell(2, 1).Value = "003";
        sheet.Cell(2, 2).Value = new DateTime(2026, 11, 10, 8, 3, 9);
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        var bytes = stream.ToArray();
        Assert.Equal(AttendanceFileFormat.Xlsx, AttendanceFileParser.DetectFormat(bytes));
        var parsed = AttendanceFileParser.Parse(bytes, AttendanceFileParser.DetectFormat(bytes));
        Assert.Equal("003", parsed.Rows[1][0]);
        Assert.True(PunchTimeParser.TryParse(parsed.Rows[1][1], out var time, out _));
        Assert.Equal(new DateTime(2026, 11, 10, 8, 3, 9), time);
    }

    [Fact]
    public void Unsupported_Binary_And_Zip_Content_Are_Rejected()
    {
        Assert.Equal(AttendanceFileFormat.Unknown, AttendanceFileParser.DetectFormat([0, 1, 2, 3, 0, 1]));
        Assert.Equal(AttendanceFileFormat.Unknown, AttendanceFileParser.DetectFormat([]));
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntry("unrelated.txt");
        }
        Assert.Equal(AttendanceFileFormat.Unknown, AttendanceFileParser.DetectFormat(stream.ToArray()));
        Assert.Throws<InvalidDataException>(() => AttendanceFileParser.Parse([], AttendanceFileFormat.Unknown));
    }

    [Fact]
    public void Excel_Explicit_Zero_Padded_Code_Format_Is_Preserved()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Attendance");
        sheet.Cell(1, 1).Value = "Biometric Code";
        sheet.Cell(1, 2).Value = "Att.Time";
        sheet.Cell(2, 1).Value = 3;
        sheet.Cell(2, 1).Style.NumberFormat.Format = "000";
        sheet.Cell(2, 2).Value = new DateTime(2026, 11, 10, 8, 3, 9);
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        var bytes = stream.ToArray();
        var parsed = AttendanceFileParser.Parse(bytes, AttendanceFileParser.DetectFormat(bytes));
        Assert.Equal("003", parsed.Rows[1][0]);
    }

    [Fact]
    public void Utf16_Xml_And_Sparse_Spreadsheet_Cells_Are_Supported()
    {
        const string xml = "<?xml version=\"1.0\" encoding=\"utf-16\"?><Workbook xmlns=\"urn:schemas-microsoft-com:office:spreadsheet\" xmlns:ss=\"urn:schemas-microsoft-com:office:spreadsheet\"><Worksheet><Table><Row><Cell><Data ss:Type=\"String\">ID</Data></Cell><Cell ss:Index=\"3\"><Data ss:Type=\"String\">Att.Time</Data></Cell></Row></Table></Worksheet></Workbook>";
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(xml)).ToArray();
        Assert.Equal(AttendanceFileFormat.SpreadsheetXml, AttendanceFileParser.DetectFormat(bytes));
        var sheet = AttendanceFileParser.Parse(bytes, AttendanceFileParser.DetectFormat(bytes));
        Assert.Equal(new[] { "ID", "", "Att.Time" }, sheet.Rows.Single());
    }

    [Fact]
    public void Legacy_Html_Fragments_And_Csv_Quoted_Names_Still_Work()
    {
        var html = Encoding.UTF8.GetBytes("<table><TR><TD>ID</TD><TD>Att.Time</TD></TR><TR><TD>003&nbsp;</TD><TD>2026-11-10 08:03:09</TD></TR></table>");
        var parsedHtml = AttendanceFileParser.Parse(html, AttendanceFileParser.DetectFormat(html));
        Assert.Equal(2, parsedHtml.Rows.Count);
        Assert.Equal("003", parsedHtml.Rows[1][0]);
        var csv = Encoding.UTF8.GetBytes("ID,Name,Att.Time\n003,\"Test, Employee\",2026-11-10 08:03:09\n");
        var parsedCsv = AttendanceFileParser.Parse(csv, AttendanceFileParser.DetectFormat(csv));
        Assert.Equal("Test, Employee", parsedCsv.Rows[1][1]);
    }

    [Fact]
    public void Local_Timestamp_Stays_Unspecified_And_Is_Not_Converted_To_Utc()
    {
        Assert.True(PunchTimeParser.TryParse("2026-11-10 08:03:09", out var time, out _));
        Assert.Equal(new DateTime(2026, 11, 10, 8, 3, 9), time);
        Assert.Equal(DateTimeKind.Unspecified, time.Kind);
        Assert.False(PunchTimeParser.TryParse("2026-11-10T08:03:09Z", out _, out var message));
        Assert.Contains("منطقة زمنية", message);
    }

    [Theory]
    [InlineData("08:03:09")]
    [InlineData("10/11 08:03:09")]
    [InlineData("November 10 08:03:09")]
    public void Partial_Timestamps_Do_Not_Invent_A_Date_Or_Year(string raw)
    {
        Assert.False(PunchTimeParser.TryParse(raw, out _, out var message));
        Assert.Contains("التاريخ", message);
    }
}
