BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118093815_PayrollAccuracyAndImmediateAdvances'
)
BEGIN
    ALTER TABLE [SalaryAdvances] ADD [RegisteredAmount] decimal(18,2) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118093815_PayrollAccuracyAndImmediateAdvances'
)
BEGIN
    ALTER TABLE [PayrollItems] ADD [AttendanceMissingDays] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118093815_PayrollAccuracyAndImmediateAdvances'
)
BEGIN
    ALTER TABLE [PayrollItems] ADD [EarlyLeaveDeduction] decimal(18,2) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118093815_PayrollAccuracyAndImmediateAdvances'
)
BEGIN
    ALTER TABLE [PayrollItems] ADD [EarlyLeaveMinutes] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118093815_PayrollAccuracyAndImmediateAdvances'
)
BEGIN
    ALTER TABLE [PayrollItems] ADD [EarnedBasicSalary] decimal(18,2) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118093815_PayrollAccuracyAndImmediateAdvances'
)
BEGIN
    ALTER TABLE [PayrollItems] ADD [EarnedDeductions] decimal(18,2) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118093815_PayrollAccuracyAndImmediateAdvances'
)
BEGIN
    ALTER TABLE [PayrollItems] ADD [EarnedGrossSalary] decimal(18,2) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118093815_PayrollAccuracyAndImmediateAdvances'
)
BEGIN
    ALTER TABLE [PayrollItems] ADD [EarnedNetSalary] decimal(18,2) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118093815_PayrollAccuracyAndImmediateAdvances'
)
BEGIN
    ALTER TABLE [PayrollItems] ADD [EarningsAsOf] datetime2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118093815_PayrollAccuracyAndImmediateAdvances'
)
BEGIN
    ALTER TABLE [PayrollItems] ADD [ShortageDeduction] decimal(18,2) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118093815_PayrollAccuracyAndImmediateAdvances'
)
BEGIN
    IF NOT EXISTS (SELECT 1 FROM [AppSettings] WHERE [Key] = N'Payroll.EarlyLeaveDeductionEnabled')
        INSERT INTO [AppSettings] ([Key], [Value], [Description], [CreatedAt], [CreatedBy])
        VALUES (N'Payroll.EarlyLeaveDeductionEnabled', N'true',
            N'خصم الانصراف المبكر بسعر الدقيقة بعد سماح الخروج، دون تقريب جديد أو خصم مزدوج',
            SYSUTCDATETIME(), N'migration');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118093815_PayrollAccuracyAndImmediateAdvances'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261118093815_PayrollAccuracyAndImmediateAdvances', N'9.0.20');
END;

COMMIT;
GO

