> Current period and financial-policy handoff — see [Payroll cycle 26–25](PAYROLL_CYCLE_26_25.md). It documents the approved proration and negative-net policies and the current advance workflow.

> Superseded salary-advance requirements: see [Prepared monthly advance register](PREPARED_ADVANCE_REGISTER.md). The final clarified workflow uses unpaid preparation, separate payment confirmation, warning-only earned amounts, and a configurable day-15 payment window.

# Two devices, flexible salary advances, and payroll loading

Implemented on 17 November 2026. Existing uncommitted attendance, import, payroll, and login work was retained. No live database update, device communication driver, or real payment was executed during development.

## Confirmed behavior changes

| Area | Before | Now |
|---|---|---|
| Advances | Day-15 cutoff; unique employee/month; full monthly salary passed into mid-period entitlement | Any day; multiple advances; verified accrued days at existing rates, less known deductions and previous paid amounts |
| Payment | Separate calculate/review/approve/pay screens | Department table, selected amounts, one confirmation, atomic payment and audit |
| Payroll save | Overlapping requests, autosave replacing the dirty-row set, stale responses overwriting newer edits | One request at a time; all dirty rows retained; row revisions preserve edits made during a request |
| Loading | Capture-phase spinner could precede canceled validation; no fetch deadline | Spinner after validation; finally cleanup; 60-second AJAX deadline; retry retains amounts and request IDs |
| Totals | Browser formulas overwrote saved totals and omitted configured duration policy | Saved financial totals remain visible until authoritative server recalculation returns |
| Financial categories | Combined salary advances and loans under a salary-advance label | Separate snapshot amounts in payroll, payslips, reports, Excel and PDF; combined legacy total retained |
| Devices | Generic connection configuration | Entry/exit roles, independent sync outcomes, per-device mappings, review history, common normalized import pipeline |

## Financial calculation and remaining policy decisions

The user explicitly approved the earned-to-date rule during this task. Payment eligibility uses the employee's recorded daily rate, or the existing policy's daily rate derived from basic salary. It includes verified completed payable work days through the selected date and explicitly paid approved overtime. Existing late/duration deduction policies are used unchanged. Absence and unpaid days do not accrue earnings, so absence is not deducted again from an already unearned day.

Available = max(0, earned verified pay + eligible approved overtime - known approved deductions - relevant approved expenses - existing attendance deductions - earlier paid advances in the same payroll month).

Draft, approved-but-unpaid and cancelled records are not counted as paid. Already settled paid amounts still reserve earnings in the same month, preventing the same month from being spent again after recalculation/reopening. Future punches and future periods do not authorize payment. Hiring/termination boundaries are respected. Original biometric punches are preserved.

The following currently show **يحتاج مراجعة** and cannot be paid through the new workflow:

- Missing or ambiguous punches; no reliable attendance; unexplained early departure.
- Paid leave, rest days and holidays needing a confirmed mid-period accrual rule.
- Approved loans requiring an installment allocation, and approved adjustments requiring a mid-period allocation.
- Approved/locked payroll months, future dates, and inactive employees.

The application has no effective-dated salary-history source for reconstructing past salary changes. Eligibility uses the currently recorded employee rate; payroll staff must review retroactive rate changes before issuing a historical-period advance. The monthly payroll policy has not been changed. New hour-rounding deduction policies have not been activated.

`Payroll.Advances.EarnedToDatePolicyApproved` can be set to `false` in AppSettings to suspend new payments. Its default is enabled following the user's explicit approval. Ambiguous rows remain blocked regardless of this switch.

Every direct payment has a client request GUID, a unique filtered database index, serializable validation/payment transaction, employee/period, actual amount, payment timestamp, actor and audit record. Retrying the same request is idempotent. A reused request with different payment details is rejected. SQL Server may reject a competing transaction with a deadlock; the user can retry safely with the same request ID. No unpaid draft is promoted to paid automatically.

Paid advances settle against the generated payroll item exactly once. Recalculation detaches the superseded settlement and attaches it to the new item. Newly paid advances remain included even when earlier payroll edits are carried forward. Sheet edits cannot remove system-recorded advance/loan recoveries. Paid and settled transactions cannot be silently cancelled; historical unpaid drafts can be cancelled with a recorded reason.

## Diagnostic evidence and performance

Code inspection and executable JavaScript regressions reproduced canceled-submit spinners, overlapping save attempts, loss of other dirty rows during autosave, stale response overwrite, HTTP/network error recovery, invalid input, and timeout handling. The initial .NET suite exposed assertions for obsolete advance restrictions and tests that assumed a single correction audit row, although dependent-value recalculation records additional audit entries. These were updated to assert the requested behavior and retained history.

Advance eligibility now batches department reads instead of repeating a full query set per employee. Scheduled loan repayment lookup uses one query rather than one query per loan. Saving payroll totals projects only required numeric fields rather than materializing full payroll items. The run's editable status is checked inside the save transaction.

Live SQL Server query durations and a production browser session were not profiled. The JavaScript tests execute the production scripts against a controlled DOM/request harness; they are not full browser end-to-end tests.

## Device integration

Configure each device's role, stable identifier/serial, IP, port, model, timezone and encrypted communication key. A device-specific biometric code maps explicitly to an existing employee. Employee names are never merge keys. The shared import matcher also honors per-device mappings for file rows carrying that device identifier.

The two devices download independently. One failure does not prevent the other device's import. Results report inserted, duplicate, invalid, unmatched and review counts separately. Work-day processing runs after both downloads, including the preceding work date for overnight checkout handling. Missing directions use the configured role; explicit conflicting directions remain unchanged and are withheld in persistent review history with original identity, timestamps and fields. Raw direction and normalized direction are stored separately. Manual imports remain available.

Repeated/full downloads are deduplicated through the shared import pipeline. Incremental cursor support is not claimed: the current driver contract does not establish a verified model-specific cursor. Records are never deleted from the devices. Device-local wall times are retained. Mismatched timezone rules are blocked rather than silently shifting timestamps. Physical clock drift is not automatically corrected.

No production driver is registered and an unsupported device cannot report a successful connection. Live communication requires both device model numbers, firmware, SDK/protocol documentation and compatibility verification, server LAN reachability, communication-key support and deployment architecture (including any COM/native SDK bitness). Confirm actual clock offsets and overnight behavior on the hardware before enabling a compatible driver. SDK-specific incremental download and physical clock-discrepancy checks remain hardware work.

## Database delivery

Migration: `20261117074343_TwoDeviceAndFlexibleAdvances`.

- Replaces the unique employee/year/month advance index with a normal index.
- Adds nullable payment request IDs and their filtered unique index.
- Adds nullable payroll financial breakdown fields; historical aggregates are not rewritten or guessed.
- Adds normalized punch direction, device role/status/error/count fields.
- Adds DeviceEmployeeMapping and DeviceSyncLog with restrictive foreign keys.
- No table/data deletion in Up. Down deliberately refuses destructive rollback; corrections require a forward migration.

Reviewed deployment SQL: `docs/two-device-flexible-advances.sql`. It assumes the existing `20261116120000_AttendanceImportDeviceSupport` migration has been applied. Apply the complete migration chain if the database is older. Model/snapshot consistency was verified without connecting to a live database.

The design-time factory in `src/HrPayroll.Web/Services/AppDbContextDesignFactory.cs` uses the same appsettings, environment settings, development user secrets and environment overrides as the web app without running the host or seeders. It no longer targets a separate DesignOnly database. To apply migrations to the configured database:

```powershell
.\.tools\dotnet-ef database update --project src/HrPayroll.Infrastructure --startup-project src/HrPayroll.Web
```

An explicit `--connection` still overrides the configured connection. Pass `-- --environment Development` to load development configuration, or `-- --contentRoot <web-project-path>` when invoking outside the solution tree. The original implementation task did not update the live database; see `PROJECT_CHECK.md` for the subsequent migration repair.

## Verification

- `dotnet restore`: passed.
- `dotnet build HrPayroll.sln`: passed, zero warnings/errors.
- `dotnet test HrPayroll.sln`: 261 passed, zero failed/skipped.
- JavaScript: 8 payroll save/request scenarios and 3 global form-loading scenarios passed.
- EF pending-model check: no pending changes.
- Migration SQL generation: passed without opening the application database.

.NET results: `tests/HrPayroll.Tests/TestResults/workflow-regression.trx` (ignored generated output). Concurrency is exercised with separate SQLite connections; SQL Server production lock/load behavior still needs deployment-environment validation.

To rerun JavaScript tests with Node.js:

```text
node tests/browser/payroll-sheet.test.cjs
node tests/browser/site-loading.test.cjs
```

## Acceptance steps

1. Apply the migration to a restored copy and start the app using that database. Sign in with the existing ManagePayroll permission.
2. Open Salary Advances, select a department and month, and verify one completed workday accrues the configured daily rate rather than a full monthly salary.
3. Enter amounts for eligible employees and choose حفظ وصرف السلف المحددة. Confirm once. Verify the actual paid amount, actor and payment date in history.
4. Issue another advance in the same month. Verify the remaining balance falls, an amount equal to the balance succeeds, and an excess/negative/invalid amount fails.
5. Simulate a failed request and retry without refreshing. Verify amounts remain and the request ID cannot produce a duplicate payment. Run concurrent payment attempts against a test employee and confirm total paid never exceeds eligibility.
6. Generate payroll. Compare السلف المصروفة and net pay in the screen, details, payslip, Excel and PDF. Edit payroll, issue another advance, then recalculate and verify the total includes both advances once. Lock the period and verify payment/edit guards.
7. Open device settings and save separate Entry and Exit devices with actual models and stable identifiers. Add device-code mappings to the same existing employee where needed.
8. Without a verified driver, connection/sync must show an explicit unavailable-driver result. With a verified test driver, synchronize both and inspect separate outcomes, repeated-import duplicates, direction-conflict history, missing punches and overnight pairing.
9. Use manual file import as fallback and confirm it honors the same employee mapping, original timestamps and duplicate protection.

## Files touched by this task

Paths below are relative to the repository. Some files already contained user changes before this task; those changes were retained.

- `.gitignore`
- `src/HrPayroll.Domain/Entities/{AttendanceDevice,DeviceEmployeeMapping,DeviceSyncLog,FinancialDocuments,PayrollItem,RawPunch}.cs`
- `src/HrPayroll.Infrastructure/Devices/{AttendanceDeviceService,AttendanceDeviceSynchronization}.cs`
- `src/HrPayroll.Infrastructure/Import/{AttendanceFileModels,AttendanceImportService}.cs`
- `src/HrPayroll.Web/Services/AppDbContextDesignFactory.cs`
- `src/HrPayroll.Infrastructure/Persistence/Configurations/{FinancialConfigurations,PayrollConfigurations,DeviceEmployeeMappingConfiguration,DeviceSyncLogConfiguration}.cs`
- `src/HrPayroll.Infrastructure/Migrations/20261117074343_TwoDeviceAndFlexibleAdvances.cs` and `.Designer.cs`; `AppDbContextModelSnapshot.cs`
- `src/HrPayroll.Infrastructure/Services/{AttendanceProcessingService,SalaryAdvanceService,SalaryAdvancePayments,PayrollService,PayrollSheetService,ReportService,ExcelExportService,PdfExportService}.cs`
- `src/HrPayroll.Web/Controllers/{AttendanceDevicesController,SalaryAdvancesController,PayrollController}.cs`
- `src/HrPayroll.Web/ViewModels/{AttendanceDeviceViewModels,SalaryAdvancesPage}.cs`
- `src/HrPayroll.Web/Views/AttendanceDevices/{Index,History}.cshtml`
- `src/HrPayroll.Web/Views/SalaryAdvances/Index.cshtml`
- `src/HrPayroll.Web/Views/Payroll/{Index,Details,Payslip}.cshtml`
- `src/HrPayroll.Web/Views/Dashboard/Index.cshtml`
- `src/HrPayroll.Web/wwwroot/js/{site,payroll-sheet,salary-advances}.js`
- `tests/HrPayroll.Tests/{AdvanceEligibilityTests,FinancialDocumentsTests,PayrollTests,AttendanceProcessingTests,AdminAccountSeederTests,FlexibleAdvanceAndDeviceTests}.cs`
- `tests/browser/{payroll-sheet.test,site-loading.test}.cjs`
- `docs/FLEXIBLE_ADVANCES_AND_DEVICES.md`; `docs/two-device-flexible-advances.sql`
