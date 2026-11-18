using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using ExcelDataReader;

namespace HrPayroll.Infrastructure.Import;

/// <summary>
/// يكتشف نوع ملف البصمة من محتواه الفعلي ثم يستخرجه إلى صفوف نصية.
/// يعالج ملفات Excel الثنائية وملفات Excel 2003 XML التي تصدّرها أجهزة البصمة بامتداد .xls
/// بالإضافة إلى CSV وجداول HTML.
/// </summary>
public static class AttendanceFileParser
{
    static AttendanceFileParser()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>يكتشف نوع الملف من أول بايتاته.</summary>
    public static AttendanceFileFormat DetectFormat(byte[] bytes)
    {
        if (bytes.Length == 0) return AttendanceFileFormat.Unknown;
        if (bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] == 0x03 && bytes[3] == 0x04)
        {
            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
                return archive.GetEntry("xl/workbook.xml") is not null
                    ? AttendanceFileFormat.Xlsx : AttendanceFileFormat.Unknown;
            }
            catch (InvalidDataException) { return AttendanceFileFormat.Unknown; }
        }

        if (bytes.Length >= 8 && bytes[0] == 0xD0 && bytes[1] == 0xCF && bytes[2] == 0x11 && bytes[3] == 0xE0)
            return AttendanceFileFormat.Biff8;

        var text = DecodeText(bytes).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        if (text.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t'))
        {
            // One damaged text record must not hide intact ZK records around it.
            // Only a positively recognized export permits this recovery; arbitrary
            // binary content is still rejected. The service rejects damaged rows.
            var intactRows = text.Split('\n')
                .Select(line => line.TrimEnd('\r'))
                .Where(line => !line.Any(c => char.IsControl(c) && c != '\t'))
                .Select(line => line.Split('\t')).ToList();
            return ZkAttendanceFormatDetector.Detect(intactRows) is not null
                ? AttendanceFileFormat.Csv : AttendanceFileFormat.Unknown;
        }
        if (text.StartsWith('<'))
        {
            var head = text[..Math.Min(text.Length, 4096)].ToLowerInvariant();
            if (head.Contains("urn:schemas-microsoft-com:office:spreadsheet"))
                return AttendanceFileFormat.SpreadsheetXml;
            if (head.Contains("<html") || head.Contains("<table"))
                return AttendanceFileFormat.HtmlTable;
            return AttendanceFileFormat.Unknown;
        }
        // Single-column CSV is also used by the existing department importer.
        return text.Length > 0 ? AttendanceFileFormat.Csv : AttendanceFileFormat.Unknown;
    }

    /// <summary>يستخرج الصفوف من الملف.</summary>
    public static ParsedSheet Parse(byte[] bytes, AttendanceFileFormat format)
    {
        return format switch
        {
            AttendanceFileFormat.Xlsx or AttendanceFileFormat.Biff8 => ParseWithExcelDataReader(bytes, format),
            AttendanceFileFormat.SpreadsheetXml => ParseSpreadsheetXml(bytes),
            AttendanceFileFormat.HtmlTable => ParseHtmlTable(bytes),
            AttendanceFileFormat.Csv => ParseCsv(bytes),
            _ => throw new InvalidDataException("محتوى الملف غير مدعوم. اختر ملف بصمة نصياً أو Excel أو XML صالحاً.")
        };
    }

    private static ParsedSheet ParseWithExcelDataReader(byte[] bytes, AttendanceFileFormat format)
    {
        using var ms = new MemoryStream(bytes);
        using var reader = ExcelReaderFactory.CreateReader(ms);
        var rows = new List<string[]>();
        var max = 0;
        do
        {
            while (reader.Read())
            {
                var arr = new string[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                    arr[i] = FormatCell(reader.GetValue(i), reader.GetNumberFormatString(i));
                max = Math.Max(max, arr.Length);
                rows.Add(arr);
            }
        } while (reader.NextResult());

        return new ParsedSheet { Format = format, Rows = rows, ColumnCount = max };
    }

    private static string FormatCell(object? value, string? numberFormat)
    {
        if (value is null) return string.Empty;
        // Some rosters store the biometric code numerically with a "00000" display
        // format. Preserve those explicit leading zeroes without applying arbitrary
        // Excel formatting to dates, money, or general numeric values.
        if (value is double numeric && numberFormat is { Length: > 1 and <= 32 }
            && numberFormat.All(c => c == '0') && numeric == Math.Truncate(numeric))
            return numeric.ToString(numberFormat, CultureInfo.InvariantCulture);
        return value switch
        {
            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            double d => d.ToString(CultureInfo.InvariantCulture),
            decimal m => m.ToString(CultureInfo.InvariantCulture),
            float f => f.ToString(CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };
    }

    private static ParsedSheet ParseSpreadsheetXml(byte[] bytes)
    {
        var rows = new List<string[]>();
        var max = 0;
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreWhitespace = true };
        using var ms = new MemoryStream(bytes);
        using var reader = XmlReader.Create(ms, settings);

        var currentRow = new List<string>();
        var inCell = false;
        var cellIsString = false;
        var inData = false;
        var cellBuffer = new StringBuilder();
        string? cellIndex = null;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "Row":
                        currentRow = new List<string>();
                        break;
                    case "Cell":
                        inCell = true;
                        cellBuffer.Clear();
                        cellIndex = reader.GetAttribute("Index") ?? reader.GetAttribute("Index", "urn:schemas-microsoft-com:office:spreadsheet");
                        var type = reader.GetAttribute("Type") ?? reader.GetAttribute("Type", "urn:schemas-microsoft-com:office:spreadsheet");
                        cellIsString = string.Equals(type, "String", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "Data":
                        inData = true;
                        break;
                }
            }
            else if (reader.NodeType == XmlNodeType.Text && inData && inCell)
            {
                cellBuffer.Append(reader.Value);
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                switch (reader.LocalName)
                {
                    case "Data":
                        inData = false;
                        break;
                    case "Cell":
                        inCell = false;
                        // احترام فهارس الخلايا (Index) لملء الفراغات
                        if (cellIndex is not null && int.TryParse(cellIndex, out var idx1))
                        {
                            while (currentRow.Count < idx1 - 1) currentRow.Add(string.Empty);
                        }
                        currentRow.Add(cellBuffer.ToString());
                        cellIndex = null;
                        break;
                    case "Row":
                        max = Math.Max(max, currentRow.Count);
                        rows.Add(currentRow.ToArray());
                        break;
                }
            }
        }

        return new ParsedSheet { Format = AttendanceFileFormat.SpreadsheetXml, Rows = rows, ColumnCount = max };
    }

    private static ParsedSheet ParseHtmlTable(byte[] bytes)
    {
        var text = DecodeText(bytes);
        var rows = new List<string[]>();
        var max = 0;
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreWhitespace = true };
        try
        {
            using var stringReader = new StringReader(text);
            using var reader = XmlReader.Create(stringReader, settings);
            var row = new List<string>();
            var buffer = new StringBuilder();
            var inCell = false;
            while (reader.Read())
            {
                var elementName = reader.LocalName.ToLowerInvariant();
                if (reader.NodeType == XmlNodeType.Element)
                {
                    if (elementName == "tr") { row = new List<string>(); }
                    else if (elementName is "td" or "th") { inCell = true; buffer.Clear(); }
                }
                else if (reader.NodeType == XmlNodeType.Text && inCell) buffer.Append(reader.Value);
                else if (reader.NodeType == XmlNodeType.EndElement)
                {
                    if (elementName is "td" or "th") { inCell = false; row.Add(buffer.ToString().Trim()); }
                    else if (elementName == "tr")
                    {
                        if (row.Count > 0) { max = Math.Max(max, row.Count); rows.Add(row.ToArray()); }
                    }
                }
            }
        }
        catch
        {
            rows.Clear();
            max = 0;
            // Legacy exports are often HTML fragments, not well-formed XML.
            foreach (Match rowMatch in Regex.Matches(text, @"<tr\b[^>]*>(.*?)</tr\s*>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(1)))
            {
                var cells = Regex.Matches(rowMatch.Groups[1].Value, @"<t[dh]\b[^>]*>(.*?)</t[dh]\s*>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(1))
                    .Cast<Match>()
                    .Select(cell => WebUtility.HtmlDecode(Regex.Replace(cell.Groups[1].Value, "<[^>]+>", "",
                        RegexOptions.Singleline, TimeSpan.FromSeconds(1))).Trim()).ToArray();
                if (cells.Length > 0) { max = Math.Max(max, cells.Length); rows.Add(cells); }
            }
        }
        return new ParsedSheet { Format = AttendanceFileFormat.HtmlTable, Rows = rows, ColumnCount = max };
    }

    private static ParsedSheet ParseCsv(byte[] bytes)
    {
        var text = DecodeText(bytes, out var encodingName);
        var rows = new List<string[]>();
        var max = 0;
        var delimiter = DetectDelimiter(text);
        var tabRows = delimiter == '\t'
            ? text.Split('\n').Select(line => line.TrimEnd('\r').Split('\t')).ToList()
            : null;
        // ZK text records use literal tab-separated fields; a quote in a name must
        // never absorb the next valid attendance record as a multiline CSV value.
        var records = tabRows is not null && ZkAttendanceFormatDetector.Detect(tabRows) is not null
            ? tabRows : SplitCsv(text, delimiter);
        foreach (var line in records)
        {
            if (line.Length == 1 && string.IsNullOrWhiteSpace(line[0])) continue;
            max = Math.Max(max, line.Length);
            rows.Add(line);
        }
        return new ParsedSheet { Format = AttendanceFileFormat.Csv, Rows = rows, ColumnCount = max, EncodingName = encodingName };
    }

    private static string DecodeText(byte[] bytes)
        => DecodeText(bytes, out _);

    private static string DecodeText(byte[] bytes, out string encodingName)
    {
        Encoding encoding;
        var offset = 0;
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        { encoding = Encoding.Unicode; offset = 2; }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        { encoding = Encoding.BigEndianUnicode; offset = 2; }
        else if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        { encoding = new UTF8Encoding(false, true); offset = 3; }
        else
        {
        // جرّب UTF-8 فإن فشل استخدم نافذة عربية (كثير من أجهزة البصمة تصدّر windows-1256)
            try
            {
                var result = new UTF8Encoding(false, true).GetString(bytes);
                encodingName = "utf-8";
                return result;
            }
            catch (DecoderFallbackException) { encoding = Encoding.GetEncoding(1256); }
        }
        encodingName = encoding.WebName;
        return encoding.GetString(bytes, offset, bytes.Length - offset);
    }

    private static char DetectDelimiter(string text)
    {
        var counts = new Dictionary<char, int> { ['\t'] = 0, [';'] = 0, [','] = 0 };
        foreach (var line in text.Split('\n').Where(line => !string.IsNullOrWhiteSpace(line)).Take(20))
        {
            var quoted = false;
            foreach (var c in line)
            {
                if (c == '"') quoted = !quoted;
                else if (!quoted && counts.ContainsKey(c)) counts[c]++;
            }
        }
        if (counts['\t'] > 0) return '\t';
        return counts[';'] > counts[','] ? ';' : ',';
    }

    /// <summary>تقسيم CSV مع دعم علامات التنصيص.</summary>
    private static IEnumerable<string[]> SplitCsv(string text, char delim)
    {
        var current = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == delim) { current.Add(sb.ToString().Trim()); sb.Clear(); }
                else if (c == '\n') { current.Add(sb.ToString().Trim()); sb.Clear(); yield return current.ToArray(); current = new List<string>(); }
                else if (c == '\r') { }
                else sb.Append(c);
            }
        }
        if (sb.Length > 0 || current.Count > 0)
        {
            current.Add(sb.ToString().Trim());
            yield return current.ToArray();
        }
    }
}
