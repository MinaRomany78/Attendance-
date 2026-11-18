namespace HrPayroll.Infrastructure.Import;

/// <summary>
/// Identifies the documented eight-field Attendance Management export. The other
/// four fields are deliberately not interpreted as device or record identities.
/// </summary>
public static class ZkAttendanceFormatDetector
{
    public static ColumnMapping? Detect(IReadOnlyList<string[]> rows)
    {
        // A malformed row must not prevent valid later rows being recognized.
        if (!rows.Any(row => row.Length == 8
            && !string.IsNullOrWhiteSpace(row[0])
            && HasTimestamp(row[1])
            && (string.Equals(row[5].Trim(), "I", StringComparison.OrdinalIgnoreCase)
                || string.Equals(row[5].Trim(), "O", StringComparison.OrdinalIgnoreCase))))
            return null;

        return new ColumnMapping
        {
            HeaderRowIndex = -1, EmployeeCode = 0, AttTime = 1, Name = 4,
            AttType = 5, ExpectedColumnCount = 8, IsZkExport = true
        };
    }

    internal static bool HasTimestamp(string value)
        => (value.Contains(':') || (value.Trim().Length == 14 && value.Trim().All(char.IsDigit)))
            && PunchTimeParser.TryParse(value, out _, out _);
}
