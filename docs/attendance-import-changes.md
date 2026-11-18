# Attendance import implementation inventory

All paths are relative to the repository root. Existing employee-import services and payroll policies remain in place. `src/HrPayroll.Web/Views/Account/Login.cshtml` already had local changes before this work and was not edited as part of this task.

## Domain

- `src/HrPayroll.Domain/Entities/AttendanceDevice.cs` — new saved device configuration.
- `src/HrPayroll.Domain/Entities/ImportBatch.cs` — separate review-row statistics.
- `src/HrPayroll.Domain/Entities/RawPunch.cs` — original source fields for diagnostics.
- `src/HrPayroll.Domain/Enums/Enums.cs` — review status without changing existing values.
- `src/HrPayroll.Domain/Services/PunchClassifier.cs` — preserve explicit and opposite directions; flag incomplete/ambiguous cases.

## Import and device infrastructure

- `src/HrPayroll.Infrastructure/HrPayroll.Infrastructure.csproj` — shared ASP.NET framework for Data Protection.
- `src/HrPayroll.Infrastructure/Import/AttendanceFileModels.cs` — encoding/layout/original-field contracts.
- `src/HrPayroll.Infrastructure/Import/AttendanceFileParser.cs` — content detection, Arabic decoding, valid-row recovery and legacy format preservation.
- `src/HrPayroll.Infrastructure/Import/ColumnMapper.cs` — headerless/explicit-header mapping and safe local timestamp parsing.
- `src/HrPayroll.Infrastructure/Import/ZkAttendanceFormatDetector.cs` — dedicated eight-column layout detection.
- `src/HrPayroll.Infrastructure/Import/AttendanceImportService.cs` — shared normalization, employee matching, duplicate preview and transactional commit.
- `src/HrPayroll.Infrastructure/Import/EmployeeAttendanceMatcher.cs` — exact biometric-first, ambiguity-aware matching.
- `src/HrPayroll.Infrastructure/Import/AttendanceEmployeeMappingService.cs` — authorized, audited missing-code resolution.
- `src/HrPayroll.Infrastructure/Import/ZkRosterReconciliationService.cs` — read-only roster comparison.
- `src/HrPayroll.Infrastructure/Devices/IZkDeviceClient.cs` — model-specific driver contracts; no concrete protocol assumed.
- `src/HrPayroll.Infrastructure/Devices/AttendanceDeviceService.cs` — configuration, protected keys, compatibility checks and preview bridge.
- `src/HrPayroll.Infrastructure/Services/AttendanceProcessingService.cs` — overnight continuation fixes and preservation of manual/locked payroll results.

## Additive database definition

- `src/HrPayroll.Infrastructure/Persistence/AppDbContext.cs` — device DbSet.
- `src/HrPayroll.Infrastructure/Persistence/Configurations/AttendanceDeviceConfiguration.cs` — device field bounds.
- `src/HrPayroll.Infrastructure/Migrations/20261116120000_AttendanceImportDeviceSupport.cs` — new device table, nullable original fields, review count default zero; unapplied.
- `src/HrPayroll.Infrastructure/Migrations/20261116120000_AttendanceImportDeviceSupport.Designer.cs` — target model.
- `src/HrPayroll.Infrastructure/Migrations/AppDbContextModelSnapshot.cs` — synchronized model snapshot.

## MVC and presentation

- `src/HrPayroll.Web/Controllers/ImportController.cs` — combined and legacy uploads, secure preview/confirm, mapping, validation export, cancellation and roster actions.
- `src/HrPayroll.Web/Controllers/AttendanceDevicesController.cs` — device configuration and supported-only connection testing.
- `src/HrPayroll.Web/Services/AttendanceImportStagingStore.cs` — encrypted, expiring owner-bound upload staging and mapping snapshots.
- `src/HrPayroll.Web/ViewModels/ImportViewModels.cs` — combined mode and summary counts.
- `src/HrPayroll.Web/ViewModels/AttendanceDeviceViewModels.cs` — validated configuration form.
- `src/HrPayroll.Web/Views/Import/Index.cshtml` — Arabic RTL import methods, upload cards, devices and history.
- `src/HrPayroll.Web/Views/Import/Preview.cshtml` — all-row validation, filters, mapping, summaries, export and confirmation.
- `src/HrPayroll.Web/Views/Import/Details.cshtml` — saved batch statistics and original fields.
- `src/HrPayroll.Web/Views/Import/Roster.cshtml` — read-only roster report.
- `src/HrPayroll.Web/Views/AttendanceDevices/Index.cshtml` — honest configured/unsupported driver states.
- `src/HrPayroll.Web/wwwroot/css/attendance-import.css` — scoped responsive light/dark styling using existing tokens.
- `src/HrPayroll.Web/wwwroot/js/attendance-import.js` — drag/drop, upload feedback/progress and client-side filters.
- `src/HrPayroll.Web/Program.cs` — device/Data Protection services and explicit production migration guard.
- `src/HrPayroll.Web/appsettings.json` — automatic startup migration disabled by default.

## Regression tests

- `tests/HrPayroll.Tests/ZkAttendanceParserTests.cs`
- `tests/HrPayroll.Tests/ZkAttendanceImportTests.cs`
- `tests/HrPayroll.Tests/ZkEmployeeMappingTests.cs`
- `tests/HrPayroll.Tests/ZkDeviceTests.cs`
- `tests/HrPayroll.Tests/AttendanceImportWorkflowTests.cs`
- `tests/HrPayroll.Tests/AttendanceCommitFailureTests.cs`
- `tests/HrPayroll.Tests/AttendanceProcessingTests.cs`
- `tests/HrPayroll.Tests/ClassificationTests.cs`
- `tests/HrPayroll.Tests/SchemaConsistencyTests.cs`

## Documentation

- `ATTENDANCE_IMPORT.md` — root causes, workflow, actual-file testing, migration review and verification.
- `docs/attendance-devices.md` — device compatibility, LAN/IIS, SDK and Data Protection requirements.
- `docs/attendance-import-changes.md` — this complete inventory.
