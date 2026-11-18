BEGIN TRANSACTION;
ALTER TABLE [SalaryAdvances] ADD [CycleKey] nvarchar(30) NULL;

ALTER TABLE [SalaryAdvances] ADD [PreparedAmount] decimal(18,2) NULL;

ALTER TABLE [SalaryAdvances] ADD [PreparedAt] datetime2 NULL;

ALTER TABLE [SalaryAdvances] ADD [PreparedBy] nvarchar(200) NULL;

ALTER TABLE [SalaryAdvances] ADD [Revision] bigint NOT NULL DEFAULT CAST(0 AS bigint);

CREATE UNIQUE INDEX [IX_SalaryAdvances_EmployeeId_Year_Month_CycleKey] ON [SalaryAdvances] ([EmployeeId], [Year], [Month], [CycleKey]) WHERE [CycleKey] IS NOT NULL;

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261117083643_PreparedMonthlyAdvanceRegister', N'9.0.20');

COMMIT;
GO

