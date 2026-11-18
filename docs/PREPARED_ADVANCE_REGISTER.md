> Current period and financial-policy handoff — see [Payroll cycle 26–25](PAYROLL_CYCLE_26_25.md). It documents the approved proration and negative-net policies and the current advance workflow.

﻿# Prepared monthly salary advance register — final clarified requirements

This document supersedes the salary-advance rules in FLEXIBLE_ADVANCES_AND_DEVICES.md. Device, attendance-correction and loading fixes from that implementation remain in place. No production database was migrated and no real payments or Identity resets were performed.

## Delivered workflow

Open **سلف الموظفين**, choose a department and month, enter amounts, then select **حفظ كشف السلف**. Saving creates or updates a **Prepared / معدة ولم تصرف** monthly register row. It records the amount, preparation time and administrator without recording a payment or payroll deduction. Row-level Save and bulk Save use the same service.

Earned pay, worked days, earlier paid advances and remaining earned pay are informational. Any valid positive currency amount can be prepared and confirmed, including an amount greater than current earnings. The warning is **قيمة السلفة أكبر من المستحق الحالي للموظف.** The application neither reduces the amount nor enforces earned pay as a cap. Uncertain attendance is labelled as an estimate requiring review and does not block an administrator's requested advance.

After physically paying employees, select the saved rows and use **تأكيد صرف السلف**, or use the row's confirmation button. Confirmation records the saved amount, actual confirmation/payment timestamp, payer and audit event. It requires the saved revision and amount; changing the input requires saving first. Selected rows are confirmed in one transaction. A failed row rolls back the batch. Repeated requests with the same payment request ID are idempotent; a different confirmation for an already paid row is rejected. Paid records cannot be edited through the register.

Partial payment is deliberately not offered for this monthly-cycle workflow. The entire saved amount must be confirmed. To pay a smaller amount, edit and save the unpaid prepared amount before physically paying and confirming it. Historical paid amounts and existing settlement links remain unchanged.

## Monthly cycle and dates

There is one new register row per employee/year/month with CycleKey `mid-month`. Repeated Save updates that row rather than creating another advance cycle. A filtered unique index enforces this in the database. A revision concurrency token and serializable transactions prevent stale saves and save/payment races. Existing historical advances retain a null CycleKey. An existing active historical record in the same month is reported as a conflict rather than silently replaced or duplicated.

Preparation is allowed before payment day. Payment uses the company's configured timezone and the server's current date. The provisional payment window is **day 15 through day 15**. The same page lets an authorized payroll user configure the allowed start/end days, with an audit entry. Confirmation must occur inside that window and in the register's own calendar month. The workflow does not silently backdate confirmations or transfer a deduction to another month.

Settings:

- `Payroll.Advances.PaymentWindowStartDay` — default 15.
- `Payroll.Advances.PaymentWindowEndDay` — default 15.

The former earned-limit approval setting and immediate-payment action no longer control the new workflow. Old calculate/review/approve/pay controller actions are excluded from MVC action routing; historical lifecycle data and service support are retained.

## Payroll and attendance integrity

Payroll selects only actual Paid advances with no current settlement item, in the same employee/year/month. Prepared, cancelled, calculated and approved-but-unpaid amounts are excluded. Regeneration replaces settlement links rather than charging twice. Approved/locked payroll blocks preparation/payment changes and reports the conflict.

The payroll table now has independent **السلف المصروفة** and **أقساط القروض** columns. The historical combined financial aggregate remains available for reconciliation, and salary advances, loan installments, employee expenses and other deductions keep their existing separate snapshot/report fields. The automatic advance/loan aggregate cannot be manually increased or decreased for modern payroll rows; this prevents double-counting a confirmed payment through an editable aggregate. Historical unsplit totals are preserved and labelled as historical rather than guessed.

The previously implemented shared attendance recalculation remains responsible for lateness, early departure, actual work duration, raw overtime and derived indicators after manual correction. Original biometric records and approved overrides remain preserved. The existing two-device tests cover entry/exit roles, explicit code mappings, independent failures, deduplication, conflict retention and overnight pairing. No hardware protocol driver is fabricated.

Earned-to-date uses recorded daily rates and processed eligible days through the current company date; future records do not accrue pay. Approved paid leave can contribute eligible payable days while actual worked-day counts remain separate. Missing punches, partial attendance and ambiguous payable-day allocations are explicitly marked for review. There is no effective-dated salary history for reconstructing retroactive salary changes; the current recorded rate is used for informational accrual. Existing financial deduction policies remain active.

## Loading, display rounding and diagnosed causes

Existing fixes prevent overlapping payroll saves, loss of other dirty rows during autosave, capture-phase spinners on cancelled form submissions and stale responses overwriting newer edits. Payroll totals come from the saved server snapshot. Request timeouts and finally cleanup restore buttons after recoverable errors.

The new register preserves input values on request failure, keeps payment request IDs for safe retries, disables controls while an operation runs, and warns before navigating away with unsaved edits. Confirmation requires saved amounts and prevents a reload from losing other unsaved row edits.

Settings now expose display rounding Up / Down / Nearest with 0.25 / 0.5 / 1-hour increments:

- `Attendance.DisplayHourRoundingMode`.
- `Attendance.DisplayHourRoundingIncrement`.

Attendance details use these display settings while raw minutes remain intact. Neither RoundedAttendanceDeductionsEnabled nor RoundedOvertimeEnabled is activated by these settings. Nine regression cases verify that each display-rounding combination leaves financial deduction amounts unchanged.

Live production query timing and a real browser session were not profiled. Server tests use isolated SQLite databases; concurrency tests use separate connections. SQL Server production locking/load behavior and actual device communication require deployment-environment verification.

## Additive database migration

`20261117083643_PreparedMonthlyAdvanceRegister` adds nullable CycleKey, PreparedAmount, PreparedAt and PreparedBy, a Revision column defaulting to zero, and a filtered unique employee/year/month/cycle index. Existing financial values and statuses are not rewritten. No existing migrations were removed. Destructive rollback is refused; schema corrections require a reviewed forward migration.

SQL: `docs/prepared-monthly-advance-register.sql`. It applies after `20261117074343_TwoDeviceAndFlexibleAdvances`; databases behind that version need the existing earlier migration chain too. No schema update was applied to production automatically.

## Verification results

- `dotnet restore`: passed.
- `dotnet build HrPayroll.sln`: passed with zero warnings/errors.
- `dotnet test HrPayroll.sln`: **275 passed, zero failed, zero skipped**.
- JavaScript: **17 scenarios passed** across payroll, global form loading, and the prepared register.
- EF pending-model check: no changes since the migration snapshot.
- Migration SQL generated without updating an application database.

New coverage includes prepared/editable amounts, below/equal/above-earned amounts, warning without blocking, payment window boundaries, configurable windows, exact saved-amount confirmation, duplicate payment protection, unpaid payroll exclusion, correct same-month deduction, regeneration, stale requests, separate-connection concurrency, locked payroll and explicit rejection of unsupported partial payments.

Rerun JavaScript checks with Node.js:

```text
node tests/browser/payroll-sheet.test.cjs
node tests/browser/site-loading.test.cjs
node tests/browser/salary-advances.test.cjs
```

## Manual verification

1. Apply the complete migration chain to a restored test database and start the app against it. Sign in using the existing ManagePayroll permission.
2. Open Salary Advances and select a department. Confirm the employee list, attendance information and existing advance history.
3. Enter a positive amount larger than the displayed earned amount. Confirm that the Arabic warning appears and Save succeeds without reducing the amount.
4. Save the same list again. Verify one employee/monthly-cycle row exists and PaidAmount remains zero. Edit and save it; verify the audit history records the change.
5. Generate payroll before confirmation. Verify the prepared advance deducts zero.
6. In the test database, configure a payment window including today's company-local day if today is not day 15. Confirm selected saved advances after recording test physical-payment delivery. Verify payer/date, actual paid amount, and immutable Paid status.
7. Repeat the request or confirmation. Verify no second payment is recorded. Try editing the paid amount; it must be rejected.
8. Generate and regenerate the same month's payroll. Compare salary advances and net pay in the table, details, payslip, Excel and PDF. Verify each paid amount is deducted once and remains in the correct month.
9. Lock the payroll period and attempt another payment/change. Verify an explicit conflict and no silent month transfer.
10. Change display-rounding mode/increment in Settings. Verify attendance display changes while saved financial deductions remain unchanged.
11. Configure the Entry and Exit devices. Without a verified driver, connection/sync must report its unavailability. With verified test hardware/driver, check both devices, one offline, repeated downloads, conflicts and overnight checkout. Manual file import remains available.

## Remaining hardware requirements

Both device models, firmware versions, official SDK or documented compatible protocol, communication-key support, server LAN access and native/COM deployment bitness still need confirmation. No production Ethernet driver is registered. Physical clock-drift correction and SDK-specific incremental cursors are not claimed without verified model support. Device logs are never deleted by synchronization.

## Files changed in this clarification

- `src/HrPayroll.Domain/Entities/FinancialDocuments.cs`
- `src/HrPayroll.Infrastructure/Persistence/Configurations/FinancialConfigurations.cs`
- `src/HrPayroll.Infrastructure/Migrations/20261117083643_PreparedMonthlyAdvanceRegister.cs` and `.Designer.cs`
- `src/HrPayroll.Infrastructure/Migrations/AppDbContextModelSnapshot.cs`
- `src/HrPayroll.Infrastructure/Services/SalaryAdvanceRegisterService.cs` (new)
- `src/HrPayroll.Infrastructure/Services/SalaryAdvancePayments.cs`
- `src/HrPayroll.Infrastructure/Services/SalaryAdvanceService.cs`
- `src/HrPayroll.Infrastructure/Services/PayrollSheetService.cs`
- `src/HrPayroll.Infrastructure/Services/SettingsService.cs`
- `src/HrPayroll.Infrastructure/Services/ExcelExportService.cs`
- `src/HrPayroll.Web/Controllers/SalaryAdvancesController.cs`
- `src/HrPayroll.Web/Controllers/SettingsController.cs`
- `src/HrPayroll.Web/ViewModels/SalaryAdvancesPage.cs`
- `src/HrPayroll.Web/Views/SalaryAdvances/Index.cshtml`
- `src/HrPayroll.Web/Views/Payroll/Index.cshtml`
- `src/HrPayroll.Web/Views/Settings/Index.cshtml`
- `src/HrPayroll.Web/wwwroot/js/salary-advances.js`
- `tests/HrPayroll.Tests/PreparedAdvanceRegisterTests.cs` (new)
- `tests/HrPayroll.Tests/DisplayHourRoundingTests.cs` (new)
- `tests/HrPayroll.Tests/AdvanceEligibilityTests.cs`
- `tests/HrPayroll.Tests/FlexibleAdvanceAndDeviceTests.cs`
- `tests/browser/salary-advances.test.cjs` (new)
- `docs/PREPARED_ADVANCE_REGISTER.md` (this file), `docs/prepared-monthly-advance-register.sql`, and the superseded-report notice.

All earlier uncommitted application, employee, attendance, login and financial work was retained.
