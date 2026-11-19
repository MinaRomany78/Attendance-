# Payroll cycle 26–25 delivery

Updated 17 November 2026. This is the current handoff for the payroll cycle and approved financial policies. It supersedes the period, negative-net and advance eligibility statements in earlier handoffs. Existing workspace edits were retained.

## Approved calculations

- A payroll label identifies its closing month. November 2026 covers 2026-10-26 through 2026-11-25 inclusive. `PayrollPeriod` owns mapping, boundaries, identifiers and historical fallback.
- Full-cycle basic salary equals contractual monthly salary across 28, 29, 30 and 31 actual calendar days. Standard daily salary uses monthly salary divided by 30. Explicit employee daily-rate overrides still apply to the existing attendance deduction and overtime formulas.
- The user approved joining and leaving proration. Eligible calendar days within employment and cycle boundaries multiply contractual salary divided by 30, capped at contractual monthly salary. For a 6,000 salary and six eligible days, basic salary is 1,200 while the standard daily rate is 200.
- Actual attendance dates, worked days and overtime remain available for every date in the cycle, including the 31st. Payable basic days are a separate snapshot field.
- The user approved negative net salary on statements. Earnings of 6,000 and paid advances of 7,000 produce net salary of -1,000 and uncovered deductions of 1,000. This implementation creates no future-period recovery document from that balance.
- Earned-to-date advance information uses verified payable days through the company date, existing rates, approved paid overtime, known deductions and earlier paid advances in the exact cycle. Contract basic accrual is capped at monthly salary. Worked days and eligible payable days are separate.
- Advance earnings are informational. Any positive valid currency amount can be saved and paid, with a warning when it exceeds available earnings. Prepared rows enter payroll only after successful payment confirmation.
- Display hours and Up, Down or Nearest rounding at quarter-hour, half-hour or whole-hour increments preserve precise internal durations. Existing financial deduction settings control financial rounding.

## Root causes and fixes

| Finding | Delivered correction |
|---|---|
| Calendar-month boundaries were repeated in generation, monthly attendance, report defaults, dashboards, earned balances and locking | Shared inclusive period abstraction, stored run and advance boundaries, company-timezone defaults and actual-date overlap guards |
| Fixed basic pay and partial-cycle entitlement were conflated with actual attendance days | Separate contract basic, payable basic days and actual calendar days, with approved proration and a fixed 30-day rate basis |
| Negative net was clamped, hiding excess deductions | New cycle runs retain negative net and an explicit shortfall snapshot |
| Correction and reprocessing paths could leave dependent attendance fields stale | Shared attendance recalculation for entry, exit, lateness, early departure, work duration, shortage and overtime, preserving raw records and approved overrides |
| A correction or overtime approval after payroll generation could leave an old draft approvable | Draft attendance review flags and a financial evidence hash checked before editing or approval require explicit regeneration |
| A newly paid advance or newly approved financial source could be absent from an existing draft | Evidence includes paid advances, approved deductions, expenses, adjustments, leave, overtime, loan data and relevant contract/employment data |
| Bulk overtime locking compared calendar year/month | Bulk and single-day paths use actual stored payroll dates within serializable transactions |
| Combined workbook export selected payroll using a calendar month | Workbook selects the actual run covering its report end date and preserves legacy run dates |
| Browser totals, overlapping saves and cancelled-submit spinners could diverge from server state | Server financial totals, serialized saves, revision checks, dirty-input protection, request timeout and finally cleanup |
| Advance save and payment were coupled in the earlier workflow | One Arabic RTL register supports unpaid preparation, separate reviewed payment, payer/time audit and idempotent confirmation |
| Actual ZKTeco files and separate device responsibilities differed from generic import assumptions | Headerless format detection, encoding and column mapping, preview, employee reconciliation, deduplication, explicit Entry/Exit device roles and a shared normalized pipeline |

The Mina Romany regression uses shift 07:00–15:00, entry 08:03 and corrected exit 12:16. It verifies 63 late minutes, 164 early minutes, 253 worked minutes and 227 missing scheduled minutes. Reprocessing preserves both the correction and original exit punch.

## Payroll integrity

Generation archives prior draft versions and rebuilds financial source links in one transaction. Paid advances settle exactly once in their stored cycle. Prepared, cancelled and unpaid amounts are excluded. Modern automatic advance and loan totals reject manual replacement.

Attendance changes retain stored financial amounts and flag the affected draft for regeneration. The financial evidence hash detects later changes before spreadsheet editing or approval. It excludes settlement bookkeeping such as Paid-to-Settled status changes. Approval and locking use transactions and actual period boundaries. Finalized payroll requires the existing authorized reopening process with a recorded reason.

Historical runs with null period snapshots retain their original calendar-month scope and stored amounts. Generation refuses overlap with an active historical run. Resolve a cutover overlap through the authorized reopening/cancellation workflow and reconciliation. Historical unsettled paid advances with null cycle boundaries require reviewed assignment before new-cycle payroll generation. The migration does not guess their settlement period.

Approved leave spanning cycles is clipped to each cycle and employment range using the shared working-day calendar. Aggregate overtime crossing a cycle boundary requires day or cycle detail before allocation. Fractional leave without sufficient daily detail requires review.

## User interface

The Arabic RTL advance page shows employee, department, actual worked days, earned amount, paid advances, available amount, requested amount and status. Save prepares the register. Row, selected-row and all displayed prepared-row payment actions review employee names, amounts, count, total and payroll dates before confirmation. The register shows the full department/search result. A request supports up to 1,000 rows with an explicit limit message.

Paid rows preserve their saved amount, payer and payment time. Unpaid cancellation records a reason. Excel export respects department selection and print uses the displayed register. Payroll, payslip, dashboard and export headers show actual cycle dates. Payroll and exports retain separate advance, loan and expense categories.

## Database changes

| Migration | Purpose |
|---|---|
| `20261116120000_AttendanceImportDeviceSupport` | Import and device evidence support |
| `20261117074343_TwoDeviceAndFlexibleAdvances` | Device roles, mappings, sync history and financial category snapshots |
| `20261117083643_PreparedMonthlyAdvanceRegister` | Prepared amount, preparation audit, revision and filtered unique employee/closing-month/cycle register index |
| `20261117090718_PayrollCycle26To25` | Nullable run and advance boundaries, contract basic salary, payable days, review reason and uncovered deduction snapshots |
| `20261117123827_PayrollFinancialEvidenceGuard` | Nullable 64-character run evidence hash |

`payroll-cycle-upgrade.sql` is an idempotent SQL Server upgrade from the existing initial migration through the latest migration. Review its migration history checks and schema changes against the target database. Existing historical monetary values and attendance records are retained. The cycle migration rejects destructive rollback and requires a reviewed forward correction.

## Verification

- `dotnet restore HrPayroll.sln` completed successfully.
- `dotnet build HrPayroll.sln --no-restore` succeeded with zero warnings and zero errors.
- `dotnet test HrPayroll.sln --no-build --logger 'trx;LogFileName=payroll-cycle-final.trx'` passed all 307 tests with zero skipped or failed tests.
- EF reports no model changes pending after the latest migration.
- `git diff --check` passed.
- All 23 JavaScript scenarios passed, comprising eight payroll save cases, three global loading cases and twelve prepared register/payment review cases.

The .NET suite covers boundaries and year transitions, February and leap years, full salary across all cycle lengths, proration, precise corrected checkout, paid and unpaid advances, excess advances, retry/concurrency cases, regeneration, locked periods, source changes after computation, actual-cycle exports, Entry/Exit devices, missing punches, duplicates and employee matching. Database tests use isolated SQLite databases. SQL Server migration execution, production load timing, a live browser session and physical Ethernet devices require deployment-environment verification.

## Deployment

1. Back up the database and retain the currently deployed application package. Record the applied EF migration IDs and reconcile historical cutover overlaps and unsettled legacy paid advances.
2. Apply the reviewed `payroll-cycle-upgrade.sql` to a restored staging database with the initial migration already present. Check row counts, historical payroll totals, advance settlement links and Identity accounts before and after the upgrade.
3. Publish the solution and configure the production SQL connection and company timezone. Startup migration remains disabled by default, and the production startup path skips development migration/seeding.
4. Compare a complete 26–25 cycle in staging across attendance, payroll, payslip, Excel and PDF. Include an October 31 attendance row, prepared advance, paid advance, partial-cycle employee and negative statement. Test a failed save retry and an authorized reopening.
5. Apply the same reviewed migration script and application package during the approved deployment window. Keep future corrections as audited forward changes. Generate each new cycle through the normal payroll workflow after reconciling its attendance and financial sources.

## Remaining policy and hardware inputs

The material salary policies requested during this task are confirmed by the user. The advance payment window retains the configurable provisional day 15 default. HR can change its start and end days on the register. Uncertain attendance and rest/holiday accrual remain labelled for review in earned-to-date information while administrators can prepare a positive requested amount.

Retroactive earnings use the currently recorded salary because the repository lacks effective-dated salary history. Fractional leave and overtime totals crossing a cycle need supporting daily detail for reliable allocation. A cutover from already calculated calendar payroll requires reconciliation of overlapping dates.

Direct Ethernet integration remains disabled until both device models, firmware, supported protocol or official SDK, communication-key requirements, server LAN reachability and SDK bitness are verified. The repository provides interfaces and mock/test clients. Physical device behavior is unverified in this environment. See the device handoff for implementation details.

## Modified files

The complete list is in `payroll-cycle-modified-files.txt`. It includes delivered changes and pre-existing uncommitted workspace changes retained during this work. The main groups are domain period/calculation models, infrastructure payroll/attendance/advance/report services and EF migrations, MVC controllers and RTL views, browser scripts, and .NET/JavaScript regression suites.
