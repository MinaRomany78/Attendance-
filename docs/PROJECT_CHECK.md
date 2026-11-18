# Project check and migration repair

Checked 18 November 2026 against the local `localhost / HrPayrollDb` database.

## Reported error and cause

`Invalid column name 'PeriodStart'`, `'PeriodEnd'` and `'FinancialEvidenceHash'` came from an outdated database schema. Only the initial and attendance-device migrations were applied. Four subsequent migrations were present in the source but missing from the database.

The old design-time factory also pointed EF tools at `HrPayroll_DesignOnly`, while the application used `HrPayrollDb`. This made the documented migration command target a different database unless a connection override was supplied.

## Repairs

- Created a copy-only SQL Server backup and verified it with `RESTORE VERIFYONLY ... WITH CHECKSUM` before changing the schema.
- Applied the four existing forward migrations through `20261117123827_PayrollFinancialEvidenceGuard`. All six migrations are now recorded; no migrations remain pending.
- Confirmed `PayrollRuns.PeriodStart`, `PayrollRuns.PeriodEnd`, `PayrollRuns.FinancialEvidenceHash`, `SalaryAdvances.PeriodStart` and `SalaryAdvances.PeriodEnd` exist.
- Moved the EF design-time factory into the web project. It reads application configuration, environment-specific settings, development user secrets, environment variables and command-line overrides without starting the host or seeders. An empty connection produces a configuration error instead of targeting another database.
- Enabled the existing migration/seeding startup path in Development. Production automatic migration/seeding remains disabled.
- Corrected `run.bat` to use the executable's directory for configuration and static assets.
- Restored the pinned Bootstrap, jQuery, validation and Cairo assets with the existing SHA256 checks. Fixed `restore-font.ps1` so a missing Infrastructure font directory can be created on a fresh checkout.
- Updated the setup and migration instructions and added three factory regression tests.

Verified backup:

```text
C:\Program Files\Microsoft SQL Server\MSSQL16.MSSQLSERVER\MSSQL\Backup\HrPayrollDb_before_migration_20261118_C5BA0A0F963B4B8D8F2F5B7E4C979615.bak
```

The following record counts were identical before and after migration:

| Table | Records |
|---|---:|
| Employees | 251 |
| PayrollRuns | 1 |
| PayrollItems | 251 |
| SalaryAdvances | 1 |
| Loans | 0 |
| AspNetUsers | 1 |

## Verification

- Release solution build: zero warnings and errors.
- .NET suite: 310 passed, zero failed or skipped.
- JavaScript regression scripts: 23 scenarios passed across three files.
- EF model/snapshot check: no pending model changes.
- Read-only SQL Server EF queries: all 33 mapped entity tables passed, including full mapped-column selection; zero pending migrations.
- Production-mode startup: successful on a temporary localhost port; stopped after verification.
- Login page: HTTP 200 with an anti-forgery token. All seven locally referenced CSS/JavaScript assets returned HTTP 200 after restoration.

Authenticated business workflows and physical attendance devices were not exercised. Package vulnerability metadata could not be fetched during the temporary diagnostic project's restore because NuGet was unavailable from the restricted environment.

## Running after the repair

Restart the application and reopen the page that reported the SQL error:

```powershell
dotnet run --project src/HrPayroll.Web
```

For future manual updates to the configured database:

```powershell
.\.tools\dotnet-ef.exe database update --project src/HrPayroll.Infrastructure --startup-project src/HrPayroll.Web
```

Append `-- --environment Development` when the connection is configured through development settings or user secrets. A globally installed `dotnet ef` can be used in place of the local executable.
