using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace HrPayroll.Web.Services;

public sealed record StagedAttendanceFile(string Id, string Owner, string Zone, string FileName, DateTime ExpiresUtc,
    string? MappingFingerprint = null);

/// <summary>Encrypted temporary uploads. Tokens bind the owner, source mode, filename and expiry.</summary>
public sealed class AttendanceImportStagingStore
{
    private static readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "hrpayroll-attendance-v2");
    private readonly IDataProtector _tokens;
    private readonly IDataProtector _files;

    public AttendanceImportStagingStore(IDataProtectionProvider provider)
    {
        _tokens = provider.CreateProtector("HrPayroll.Attendance.Staging.Token.v1");
        _files = provider.CreateProtector("HrPayroll.Attendance.Staging.Content.v1");
    }

    public async Task<string> StageAsync(byte[] bytes, string owner, string zone, string fileName, CancellationToken ct,
        string? mappingFingerprint = null)
    {
        Directory.CreateDirectory(DirectoryPath);
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.bin"))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _)) continue;
            try { if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddHours(-24)) File.Delete(path); }
            catch (IOException) { /* Another request may be consuming or removing its upload. */ }
        }
        var item = new StagedAttendanceFile(Guid.NewGuid().ToString("N"), owner, zone,
            Path.GetFileName(fileName), DateTime.UtcNow.AddHours(2), mappingFingerprint);
        await File.WriteAllBytesAsync(Path.Combine(DirectoryPath, item.Id + ".bin"), _files.Protect(bytes), ct);
        return _tokens.Protect(JsonSerializer.Serialize(item));
    }

    private StagedAttendanceFile ReadToken(string token, string owner)
    {
        try
        {
            var item = JsonSerializer.Deserialize<StagedAttendanceFile>(_tokens.Unprotect(token));
            if (item is null || item.Owner != owner || string.IsNullOrWhiteSpace(owner)
                || item.ExpiresUtc < DateTime.UtcNow || !Guid.TryParseExact(item.Id, "N", out _)
                || item.Zone is not ("combined" or "entry" or "exit")) throw new CryptographicException();
            return item;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or ArgumentException)
        {
            throw new InvalidDataException("المعاينة غير صالحة أو انتهت صلاحيتها. أعد رفع الملف بحسابك.", ex);
        }
    }

    public async Task<(StagedAttendanceFile File, byte[] Bytes)> ReadAsync(string token, string owner, CancellationToken ct)
    {
        var file = ReadToken(token, owner);
        try
        {
            var encrypted = await File.ReadAllBytesAsync(Path.Combine(DirectoryPath, file.Id + ".bin"), ct);
            return (file, _files.Unprotect(encrypted));
        }
        catch (Exception ex) when (ex is IOException or CryptographicException)
        {
            throw new InvalidDataException("انتهت صلاحية المعاينة أو تم إلغاؤها. أعد رفع الملف.", ex);
        }
    }

    public void Delete(string token, string owner)
    {
        var file = ReadToken(token, owner);
        try { File.Delete(Path.Combine(DirectoryPath, file.Id + ".bin")); }
        catch (IOException) { /* Expired uploads are cleaned on the next upload. */ }
    }

    public string RefreshMapping(string token, string owner, string fingerprint)
        => _tokens.Protect(JsonSerializer.Serialize(ReadToken(token, owner) with { MappingFingerprint = fingerprint }));
}
