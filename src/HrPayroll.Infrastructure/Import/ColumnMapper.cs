namespace HrPayroll.Infrastructure.Import;

/// <summary>يكتشف أعمدة ملف البصمة ويربطها بالحقول الداخلية.</summary>
public static class ColumnMapper
{
    private static readonly string[] NoAliases = { "no", "nо", "seq", "serial", "#", "م", "رقم" };
    private static readonly string[] DeviceAliases = { "deviceid", "device", "devicecode", "deviceno", "جهاز", "رقمالجهاز", "mai" };
    private static readonly string[] CodeAliases = { "id", "empid", "employeeid", "employeecode", "deviceemployeecode", "biometriccode", "biometricid", "pin", "code", "userid", "user", "acno", "enrollid", "كود", "كودالموظف", "كودالبصمة", "الرقمالوظيفي", "رقمالموظف" };
    private static readonly string[] NameAliases = { "name", "employeename", "username", "الاسم", "اسم", "اسمالموظف" };
    private static readonly string[] TypeAliases = { "atttype", "type", "statetype", "punchtype", "direction", "inout", "status", "نوع", "نوعالبصمة", "نوعالحركة", "الحالة", "الاتجاه" };
    private static readonly string[] TimeAliases = { "atttime", "time", "datetime", "punchtime", "attdatetime", "recordtime", "logtime", "التاريخالوقت", "الوقت", "التاريخ", "وقتالبصمة", "تاريخالوقت" };

    public static string Normalize(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return string.Empty;
        var sb = new System.Text.StringBuilder(header.Length);
        foreach (var ch in header.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch) || ch >= '\u0600') sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>يبحث عن صف العناوين ويبني الخريطة (يدعم وجود صفوف عنوان قبل العناوين).</summary>
    public static ColumnMapping? Detect(IReadOnlyList<string[]> rows, int maxScanRows = 15)
    {
        var limit = Math.Min(rows.Count, maxScanRows);
        ColumnMapping? best = null;
        var bestScore = -1;

        for (var r = 0; r < limit; r++)
        {
            // Names and unknown metadata can literally contain "ID" or "Time".
            // Positive timestamp evidence takes precedence over header aliases.
            var fields = rows[r];
            if ((fields.Length == 8 && !string.IsNullOrWhiteSpace(fields[0])
                    && ZkAttendanceFormatDetector.HasTimestamp(fields[1]))
                || (fields.Length == 6 && !string.IsNullOrWhiteSpace(fields[2])
                    && PunchTimeParser.TryParse(fields[5], out _, out _)))
                continue;
            var map = new ColumnMapping { HeaderRowIndex = r };
            var score = 0;
            for (var c = 0; c < rows[r].Length; c++)
            {
                var h = Normalize(rows[r][c]);
                if (h.Length == 0) continue;

                if (map.AttTime is null && TimeAliases.Contains(h)) { map.AttTime = c; score += 3; continue; }
                // Device ID يُفحص قبل ID. لأن "deviceid" يحتوي على "id"
                if (map.DeviceId is null && DeviceAliases.Contains(h)) { map.DeviceId = c; score += 2; continue; }
                if (map.EmployeeCode is null && CodeAliases.Contains(h)) { map.EmployeeCode = c; score += 3; continue; }
                if (map.AttType is null && TypeAliases.Contains(h)) { map.AttType = c; score += 2; continue; }
                if (map.Name is null && NameAliases.Contains(h)) { map.Name = c; score += 1; continue; }
                if (map.No is null && NoAliases.Contains(h)) { map.No = c; score += 1; continue; }
            }

            if (map.IsUsable)
            {
                map.IsZkExport = rows[r].Length == 8 && map.EmployeeCode == 0 && map.AttTime == 1
                    && map.Name == 4 && map.AttType == 5;
                if (map.IsZkExport) map.ExpectedColumnCount = 8;
                // Concatenated exports may repeat their header after data. A later
                // header must never hide real punches appearing before it.
                if (rows.Take(r).Any(prior => map.EmployeeCode!.Value < prior.Length
                    && map.AttTime!.Value < prior.Length
                    && !string.IsNullOrWhiteSpace(prior[map.EmployeeCode.Value])
                    && PunchTimeParser.TryParse(prior[map.AttTime.Value], out _, out _)))
                    map.HeaderRowIndex = -1;
            }

            if (map.IsUsable && score > bestScore)
            {
                bestScore = score;
                best = map;
            }
        }

        return best;
    }

    /// <summary>خريطة احتياطية بالترتيب الافتراضي للأعمدة في تقرير الجهاز.</summary>
    public static ColumnMapping Fallback(IReadOnlyList<string[]> rows)
    {
        var zk = ZkAttendanceFormatDetector.Detect(rows);
        if (zk is not null) return zk;

        // Recognize the legacy layout from data, rather than assigning arbitrary columns.
        if (rows.Any(row => row.Length == 6 && !string.IsNullOrWhiteSpace(row[2])
            && PunchTimeParser.TryParse(row[5], out _, out _)))
            return new ColumnMapping
            {
                HeaderRowIndex = -1, No = 0, DeviceId = 1, EmployeeCode = 2,
                Name = 3, AttType = 4, AttTime = 5, ExpectedColumnCount = 6
            };

        return new ColumnMapping { HeaderRowIndex = -1 };
    }
}

/// <summary>يحوّل نص التاريخ/الوقت القادم من الجهاز إلى DateTime.</summary>
public static class PunchTimeParser
{
    private static readonly string[] Formats =
    {
        "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy/MM/dd HH:mm:ss", "yyyy/MM/dd HH:mm",
        "dd/MM/yyyy HH:mm:ss", "dd/MM/yyyy HH:mm", "d/M/yyyy HH:mm:ss", "d/M/yyyy H:mm",
        "MM/dd/yyyy HH:mm:ss", "M/d/yyyy H:mm:ss", "M/d/yyyy h:mm tt", "MM/dd/yyyy hh:mm:ss tt",
        "dd-MM-yyyy HH:mm:ss", "d-M-yyyy H:mm", "yyyyMMddHHmmss", "yyyyMMdd HHmmss",
        "ddd MMM d HH:mm:ss yyyy", "M/d/yyyy H:mm", "dd.MM.yyyy HH:mm:ss"
    };

    private static readonly string[] Cultures = { "en-US", "en-GB", "ar-SA", "ar-EG", "fr-FR", "invariant" };

    public static bool TryParse(string? raw, out DateTime value, out string? error)
    {
        value = default;
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "قيمة الوقت فارغة";
            return false;
        }

        var text = raw.Trim().Trim('"');

        // Device records are local wall-clock timestamps. A zone suffix must not
        // cause DateTime.TryParse to silently convert them using the IIS timezone.
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"(?:[zZ]|[+-]\d{2}:?\d{2}|\bUTC|\bGMT)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            error = "الوقت يحتوي على منطقة زمنية؛ يلزم تأكيد توقيت الجهاز المحلي قبل الاستيراد";
            return false;
        }

        // رقم تسلسلي لتاريخ Excel
        if (double.TryParse(text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var oa)
            && oa > 1 && oa < 2958466)
        {
            try { value = DateTime.FromOADate(oa); return true; }
            catch (ArgumentException) { /* ليس تاريخ Excel */ }
        }

        foreach (var cultureName in Cultures)
        {
            var culture = cultureName == "invariant"
                ? System.Globalization.CultureInfo.InvariantCulture
                : System.Globalization.CultureInfo.GetCultureInfo(cultureName);
            if (DateTime.TryParseExact(text, Formats, culture, System.Globalization.DateTimeStyles.None, out value))
            {
                value = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
                return true;
            }
            // A partial date or time must not acquire the server's current day/year.
            // Retain explicit two-digit numeric years supported by legacy exports.
            var explicitYear = System.Text.RegularExpressions.Regex.IsMatch(text, @"(?<!\d)\d{4}(?!\d)")
                || System.Text.RegularExpressions.Regex.IsMatch(text, @"\d{1,2}[-/.]\d{1,2}[-/.]\d{2}(?:\D|$)");
            if (!explicitYear)
            {
                error = "التاريخ غير مكتمل؛ يجب أن يتضمن اليوم والشهر والسنة مع وقت البصمة";
                return false;
            }
            if (DateTime.TryParse(text, culture, System.Globalization.DateTimeStyles.NoCurrentDateDefault, out value))
            {
                if (value.Date == DateTime.MinValue.Date)
                {
                    error = "وقت البصمة لا يتضمن تاريخاً صالحاً";
                    return false;
                }
                if (value.Kind != DateTimeKind.Unspecified)
                {
                    error = "الوقت يحتوي على منطقة زمنية؛ يلزم تأكيد توقيت الجهاز المحلي قبل الاستيراد";
                    return false;
                }
                value = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
                return true;
            }
        }

        error = "تعذّر تحويل التاريخ/الوقت: " + raw;
        return false;
    }
}
