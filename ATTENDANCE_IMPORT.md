# Attendance import and ZKTeco integration

The existing MVC, EF Core, attendance processing and payroll architecture is retained. Manual file import works independently of device connectivity. Employee roster reconciliation is a comparison workflow; attendance logs never create employees or update salary, department or financial information.

## Problems addressed

- The old fallback assumed the legacy six-column order (`No, Device, Employee code, Name, Type, Time`). An eight-column ZKTeco export instead places code and timestamp first, name fifth and direction sixth.
- Import started after `HeaderRowIndex`, even for files whose first row was data. Headerless mappings now use `-1` so the first punch is retained.
- The existing strict UTF-8/Windows-1256 fallback is retained and covered by Arabic regression fixtures. BOM handling, content validation and encoding diagnostics were strengthened. ASCII-only content is valid under both encodings.
- Employee dictionaries silently accepted the first duplicate biometric code. Ambiguous biometric mappings now require review; names are never identifiers. Leading zeros remain significant.
- Historical hashes alone could differ across sources or direction aliases. An additional employee/time/direction comparison recognizes overlaps and cross-source records while preserving historical hashes and distinct I/O events.
- Attendance classification grouped identical timestamps without keeping direction, and a lone explicit direction could fall through to timing rules. Explicit directions are retained and incomplete cases remain reviewable.
- The import UI required separate entry/exit uploads and lacked a unified file workflow, detailed filtering and a clear mapping-resolution path.

## File workflow

1. Sign in as an administrator or HR user authorized to manage attendance. Open **إدارة واستيراد البصمات** (`/Import`).
2. Use the default **ملف ZKTeco موحد** method. Select or drop one file, then choose **تحليل الملف**. Excel, CSV, XML and recognized legacy imports remain supported. The server inspects file content as well as validating upload limits; the filename extension is a selection hint, not the format detector. Use the file picker's all-files option for an export with a different extension.
3. Review all preview rows. The page shows biometric code, original name, matched employee, date, local time, direction, source and validation message. Search and employee/date/status filters do not change the imported data.
4. Resolve unknown codes by selecting the existing employee in the mapping section. Saving a mapping changes only that employee's biometric code, after uniqueness checks, then reanalyzes the staged files. Inspect the selected employee carefully before saving.
5. Export validation results to Excel when corrections are needed. Green rows are eligible, yellow rows require review, red rows are invalid, and gray rows are duplicates. Only eligible new rows are committed.
6. Choose **تأكيد الاستيراد**. Attendance processing can run for the discovered period using the current shift and payroll policies. Uncheck the processing option if you want to review raw punches first.
7. Inspect import history and the saved batch's counts and original fields. Reuploading the same punches should report duplicates rather than insert them again.

Uploads are encrypted in temporary storage and protected tokens bind each preview to its user, upload mode, filename and two-hour expiry. Confirmation compares employee identities with the reviewed snapshot and refuses changed mappings. Cancel removes the staged uploads. Export review results before confirming; rows withheld for validation are counted in the batch but are not inserted as valid punches.

The separate-file method is retained. Explicit row direction takes precedence: an O record in the entry upload is flagged for review, rather than relabeled as entry. Use the combined method for files containing both directions.

Device-local Egyptian timestamps are preserved as wall-clock values; they are not converted to UTC. UTC audit times such as import creation and last synchronization are separate from punch times. A short I/O sample verifies importing; it should not be used to validate a complete work shift or payroll calculation.

## Testing with actual files safely

Use an isolated test database containing a copy of the relevant employee code mappings. Keep real exports, roster files, salaries and personal information outside the repository.

For the eight-column ZKTeco structure:

| Column | Meaning used by the importer |
| --- | --- |
| 1 | Biometric employee code |
| 2 | Local punch timestamp |
| 3, 4 | Original metadata only |
| 5 | Employee name for display only |
| 6 | I = check-in; O = check-out |
| 7, 8 | Original metadata only |

Upload the original export directly, without opening and resaving it in Excel first. This preserves encoding and leading zeros. Both CRLF and LF line endings are supported. UTF-8 and Windows-1256 Arabic text are supported; headerless files do not need an inserted header.

A sanitized import-only fixture can contain these two tab-separated records:

```text
3	2026-11-10 08:03:09	101	1	موظف تجريبي	I	0	1
3	2026-11-10 08:08:24	102	1	موظف تجريبي	O	0	1
```

With exactly one existing employee whose `DeviceEmployeeCode` is `3`, the preview should report two records, one check-in and one check-out, both mapped to that employee. The first row must appear. No device/record ID is inferred from columns 3, 4, 7 or 8.

Confirm once, then analyze the same file again. Both records should be duplicates. Analyze an overlapping export and verify that only previously unseen punches are eligible. Also check a file with an unknown code, malformed timestamp and an unrecognized direction; review messages should identify each problem while valid rows remain available.

For overnight attendance, test an employee assigned an existing overnight shift with an entry before midnight and exit the next day. Inspect the shift's work date and exception results in attendance processing. Existing late arrival, early departure, overtime and payroll policies remain in force.

## Read-only roster reconciliation

Open **مطابقة قائمة موظفي الجهاز** (`/Import/Roster`) and upload a device roster or the existing employee template. A header row with explicit biometric-code and name columns is required; for example `User ID` and `Name`, or `كود البصمة` and `الاسم`. Other recognized biometric headers include `DeviceEmployeeCode`, `BiometricCode`, `Enroll ID`, `AC No` and `PIN`. When the current employee template contains both a payroll ID and a biometric code, the explicit biometric column takes priority. Do not use a headerless attendance log as a roster.

The report compares employee counts, unique device codes, duplicate codes, missing employees and name differences. Names support human review only. Reconciliation projects only identity/code/name fields from the system; salary and financial fields are not loaded. It performs no employee, salary, department or payroll writes. Resolve discrepancies deliberately through the authorized employee/mapping workflow, then run comparison again. The workflow does not assume that either roster always contains 251 employees.

## Database changes and deployment review

The additive migration is `src/HrPayroll.Infrastructure/Migrations/20261116120000_AttendanceImportDeviceSupport.cs`:

- Adds nullable `RawPunches.OriginalFieldsJson` for original source diagnostics.
- Adds `ImportBatches.ReviewRows`, defaulting historical rows to zero.
- Adds `AttendanceDevices` for device configuration and protected communication keys.

It does not delete or rewrite employees, punches, Identity tables, payroll records or historical hashes. Automated rollback is deliberately refused because dropping the new information would lose data. Old punches have no original-field diagnostics; their existing information is retained.

Review the migration and generated SQL against a restored database backup before applying it. Check the existing migration history and verify that the new table/column names do not conflict with untracked schema changes. Confirm historical employee, raw punch, payroll and Identity row counts before and after the migration on that restored database.

To generate SQL for review without applying it:

```powershell
dotnet ef migrations script 20261031101003_initalCreate 20261116120000_AttendanceImportDeviceSupport --project src/HrPayroll.Infrastructure --startup-project src/HrPayroll.Web --output attendance-import-review.sql
```

The application does not automatically migrate a production database. `Database:ApplyMigrationsOnStartup` defaults to false; explicit development opt-in is also required for startup migration/seeding. Deploy the new application and the reviewed additive schema together. Running the new code against the old schema will fail when querying the added fields/table.

## Ethernet integration boundary

`IZkDeviceClient` exposes model support, connection testing, device information, attendance/employee downloads and sync status. `AttendanceDeviceService` selects a compatible driver, applies timeouts/cancellation, protects the communication key and passes downloaded records to the same attendance analysis pipeline as files. No real driver is registered, no successful connection is simulated, and no download button is presented without working support. Device logs are never automatically cleared.

The configuration screen (`/AttendanceDevices`) stores name, IP address, TCP port, model, identifier/serial number, timezone and last successful synchronization. Saving configuration does not establish connectivity.

A real driver still requires:

- The exact device model, firmware, supported protocol and a compatible official SDK or documented protocol.
- Verified local LAN reachability from the IIS server or a dedicated local synchronization service. Internet connectivity is not required for LAN synchronization.
- An SDK license and deployment decision where applicable; native SDK bitness must match the Windows host process/service and IIS application-pool architecture.
- Verified timezone behavior, communication-key support, safe connection cleanup, timeouts and cancellation handling.
- An authenticated preview/confirm workflow for downloads and audit updates after successful commit. The shared normalization/deduplication service boundary is provided; unattended synchronization is not enabled.
- Persistent ASP.NET Core Data Protection keys accessible to the application identity, so saved device credentials remain decryptable across deployments. Do not log keys or include them in exports.

## Verification commands

```powershell
dotnet restore
dotnet build HrPayroll.sln
dotnet test HrPayroll.sln
```

Regression tests use sanitized data and isolated databases. No production database migration is needed to run those tests. The scoped JavaScript has also been exercised for upload validation/progress, duplicate-submit prevention, back-navigation reset and preview filters; a real browser check with your deployment's authentication, database and representative file sizes remains part of acceptance testing.

## Recorded verification (2026-11-16)

- `dotnet restore`: passed.
- `dotnet build HrPayroll.sln`: passed before the local application began locking Debug assemblies.
- Final `dotnet build HrPayroll.sln -c Release`: passed, zero warnings and errors.
- Full `dotnet test HrPayroll.sln -c Release`: passed, 241 tests, zero failures or skips, before the final separate-file audit regression was added.
- After that final UI/test adjustment, the Release build remained clean and `dotnet test HrPayroll.sln -c Release --no-build --filter FullyQualifiedName~AttendanceImportWorkflowTests` passed all seven workflow tests inside the sandbox.
- Tests include the two-record sample, synthetic 251-employee reconciliation, expired/tampered/other-user previews, stale employee mappings, and failed transaction cleanup.
- The existing Identity password-reset test needs access to its local ASP.NET Data Protection key directory; the full suite was rerun with that access.
- A later request for another elevated full-suite run was declined; the final workflow verification did not need that access.
- SQL Server model/snapshot consistency was checked without opening a production connection. The additive migration was not applied.

See [the complete changed-file inventory](docs/attendance-import-changes.md) and [device deployment details](docs/attendance-devices.md).
