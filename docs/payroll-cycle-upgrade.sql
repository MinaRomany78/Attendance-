BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261116120000_AttendanceImportDeviceSupport'
)
BEGIN
    ALTER TABLE [RawPunches] ADD [OriginalFieldsJson] nvarchar(max) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261116120000_AttendanceImportDeviceSupport'
)
BEGIN
    ALTER TABLE [ImportBatches] ADD [ReviewRows] int NOT NULL DEFAULT 0;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261116120000_AttendanceImportDeviceSupport'
)
BEGIN
    CREATE TABLE [AttendanceDevices] (
        [Id] int NOT NULL IDENTITY,
        [Name] nvarchar(150) NOT NULL,
        [IpAddress] nvarchar(45) NOT NULL,
        [TcpPort] int NOT NULL,
        [Model] nvarchar(150) NOT NULL,
        [Identifier] nvarchar(150) NULL,
        [TimeZoneId] nvarchar(100) NOT NULL,
        [ProtectedCommunicationKey] nvarchar(2000) NULL,
        [LastSuccessfulSyncUtc] datetime2 NULL,
        [CreatedAt] datetime2 NOT NULL,
        [CreatedBy] nvarchar(max) NULL,
        [UpdatedAt] datetime2 NULL,
        [UpdatedBy] nvarchar(max) NULL,
        CONSTRAINT [PK_AttendanceDevices] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261116120000_AttendanceImportDeviceSupport'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261116120000_AttendanceImportDeviceSupport', N'9.0.20');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117074343_TwoDeviceAndFlexibleAdvances'
)
BEGIN
    DROP INDEX [IX_SalaryAdvances_EmployeeId_Year_Month] ON [SalaryAdvances];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117074343_TwoDeviceAndFlexibleAdvances'
)
BEGIN
    ALTER TABLE [SalaryAdvances] ADD [PaymentRequestId] uniqueidentifier NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117074343_TwoDeviceAndFlexibleAdvances'
)
BEGIN
    ALTER TABLE [RawPunches] ADD [NormalizedAttType] nvarchar(max) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117074343_TwoDeviceAndFlexibleAdvances'
)
BEGIN
    ALTER TABLE [PayrollItems] ADD [EmployeeExpenseRecoveries] decimal(18,2) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117074343_TwoDeviceAndFlexibleAdvances'
)
BEGIN
    ALTER TABLE [PayrollItems] ADD [LegacyLoanDeductions] decimal(18,2) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117074343_TwoDeviceAndFlexibleAdvances'
)
BEGIN
    ALTER TABLE [PayrollItems] ADD [LoanInstallments] decimal(18,2) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117074343_TwoDeviceAndFlexibleAdvances'
)
BEGIN
    ALTER TABLE [PayrollItems] ADD [SalaryAdvancesPaid] decimal(18,2) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117074343_TwoDeviceAndFlexibleAdvances'
)
BEGIN
    ALTER TABLE [AttendanceDevices] ADD [ConnectionStatus] nvarchar(max) NOT NULL DEFAULT N'';
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117074343_TwoDeviceAndFlexibleAdvances'
)
BEGIN
    ALTER TABLE [AttendanceDevices] ADD [LastDownloadedCount] int NOT NULL DEFAULT 0;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117074343_TwoDeviceAndFlexibleAdvances'
)
BEGIN
    ALTER TABLE [AttendanceDevices] ADD [LastSyncError] nvarchar(max) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117074343_TwoDeviceAndFlexibleAdvances'
)
BEGIN
    ALTER TABLE [AttendanceDevices] ADD [Role] int NOT NULL DEFAULT 0;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117074343_TwoDeviceAndFlexibleAdvances'
)
BEGIN
    CREATE TABLE [DeviceEmployeeMapping] (
        [Id] int NOT NULL IDENTITY,
        [AttendanceDeviceId] int NOT NULL,
        [BiometricCode] nvarchar(50) NOT NULL,
        [EmployeeId] int NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [CreatedBy] nvarchar(max) NULL,
        [UpdatedAt] datetime2 NULL,
        [UpdatedBy] nvarchar(max) NULL,
        CONSTRAINT [PK_DeviceEmployeeMapping] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_DeviceEmployeeMapping_AttendanceDevices_AttendanceDeviceId] FOREIGN KEY ([AttendanceDeviceId]) REFERENCES [AttendanceDevices] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_DeviceEmployeeMapping_Employees_EmployeeId] FOREIGN KEY ([EmployeeId]) REFERENCES [Employees] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117074343_TwoDeviceAndFlexibleAdvances'
)
BEGIN
    CREATE TABLE [DeviceSyncLog] (
        [Id] int NOT NULL IDENTITY,
        [AttendanceDeviceId] int NOT NULL,
        [Successful] bit NOT NULL,
        [Error] nvarchar(2000) NULL,
        [ReviewRecordsJson] nvarchar(max) NULL,
        [Downloaded] int NOT NULL,
        [Inserted] int NOT NULL,
        [Duplicates] int NOT NULL,
        [Invalid] int NOT NULL,
        [Unmatched] int NOT NULL,
        [Review] int NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [CreatedBy] nvarchar(max) NULL,
        [UpdatedAt] datetime2 NULL,
        [UpdatedBy] nvarchar(max) NULL,
        CONSTRAINT [PK_DeviceSyncLog] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_DeviceSyncLog_AttendanceDevices_AttendanceDeviceId] FOREIGN KEY ([AttendanceDeviceId]) REFERENCES [AttendanceDevices] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117074343_TwoDeviceAndFlexibleAdvances'
)
BEGIN
    CREATE INDEX [IX_SalaryAdvances_EmployeeId_Year_Month] ON [SalaryAdvances] ([EmployeeId], [Year], [Month]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117074343_TwoDeviceAndFlexibleAdvances'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_SalaryAdvances_PaymentRequestId] ON [SalaryAdvances] ([PaymentRequestId]) WHERE [PaymentRequestId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117074343_TwoDeviceAndFlexibleAdvances'
)
BEGIN
    CREATE UNIQUE INDEX [IX_DeviceEmployeeMapping_AttendanceDeviceId_BiometricCode] ON [DeviceEmployeeMapping] ([AttendanceDeviceId], [BiometricCode]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117074343_TwoDeviceAndFlexibleAdvances'
)
BEGIN
    CREATE INDEX [IX_DeviceEmployeeMapping_EmployeeId] ON [DeviceEmployeeMapping] ([EmployeeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117074343_TwoDeviceAndFlexibleAdvances'
)
BEGIN
    CREATE INDEX [IX_DeviceSyncLog_AttendanceDeviceId_CreatedAt] ON [DeviceSyncLog] ([AttendanceDeviceId], [CreatedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117074343_TwoDeviceAndFlexibleAdvances'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261117074343_TwoDeviceAndFlexibleAdvances', N'9.0.20');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117083643_PreparedMonthlyAdvanceRegister'
)
BEGIN
    ALTER TABLE [SalaryAdvances] ADD [CycleKey] nvarchar(30) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117083643_PreparedMonthlyAdvanceRegister'
)
BEGIN
    ALTER TABLE [SalaryAdvances] ADD [PreparedAmount] decimal(18,2) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117083643_PreparedMonthlyAdvanceRegister'
)
BEGIN
    ALTER TABLE [SalaryAdvances] ADD [PreparedAt] datetime2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117083643_PreparedMonthlyAdvanceRegister'
)
BEGIN
    ALTER TABLE [SalaryAdvances] ADD [PreparedBy] nvarchar(200) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117083643_PreparedMonthlyAdvanceRegister'
)
BEGIN
    ALTER TABLE [SalaryAdvances] ADD [Revision] bigint NOT NULL DEFAULT CAST(0 AS bigint);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117083643_PreparedMonthlyAdvanceRegister'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_SalaryAdvances_EmployeeId_Year_Month_CycleKey] ON [SalaryAdvances] ([EmployeeId], [Year], [Month], [CycleKey]) WHERE [CycleKey] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117083643_PreparedMonthlyAdvanceRegister'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261117083643_PreparedMonthlyAdvanceRegister', N'9.0.20');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117090718_PayrollCycle26To25'
)
BEGIN
    ALTER TABLE [SalaryAdvances] ADD [PeriodEnd] datetime2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117090718_PayrollCycle26To25'
)
BEGIN
    ALTER TABLE [SalaryAdvances] ADD [PeriodStart] datetime2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117090718_PayrollCycle26To25'
)
BEGIN
    ALTER TABLE [PayrollRuns] ADD [PeriodEnd] datetime2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117090718_PayrollCycle26To25'
)
BEGIN
    ALTER TABLE [PayrollRuns] ADD [PeriodStart] datetime2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117090718_PayrollCycle26To25'
)
BEGIN
    ALTER TABLE [PayrollItems] ADD [ContractBasicSalary] decimal(18,2) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117090718_PayrollCycle26To25'
)
BEGIN
    ALTER TABLE [PayrollItems] ADD [FinancialReviewReason] nvarchar(1000) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117090718_PayrollCycle26To25'
)
BEGIN
    ALTER TABLE [PayrollItems] ADD [PayableBasicDays] decimal(6,2) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117090718_PayrollCycle26To25'
)
BEGIN
    ALTER TABLE [PayrollItems] ADD [UncoveredDeductions] decimal(18,2) NOT NULL DEFAULT 0.0;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117090718_PayrollCycle26To25'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261117090718_PayrollCycle26To25', N'9.0.20');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117123827_PayrollFinancialEvidenceGuard'
)
BEGIN
    ALTER TABLE [PayrollRuns] ADD [FinancialEvidenceHash] nvarchar(64) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117123827_PayrollFinancialEvidenceGuard'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261117123827_PayrollFinancialEvidenceGuard', N'9.0.20');
END;

COMMIT;
GO

