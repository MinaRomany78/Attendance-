BEGIN TRANSACTION;
DROP INDEX [IX_SalaryAdvances_EmployeeId_Year_Month] ON [SalaryAdvances];

ALTER TABLE [SalaryAdvances] ADD [PaymentRequestId] uniqueidentifier NULL;

ALTER TABLE [RawPunches] ADD [NormalizedAttType] nvarchar(max) NULL;

ALTER TABLE [PayrollItems] ADD [EmployeeExpenseRecoveries] decimal(18,2) NULL;

ALTER TABLE [PayrollItems] ADD [LegacyLoanDeductions] decimal(18,2) NULL;

ALTER TABLE [PayrollItems] ADD [LoanInstallments] decimal(18,2) NULL;

ALTER TABLE [PayrollItems] ADD [SalaryAdvancesPaid] decimal(18,2) NULL;

ALTER TABLE [AttendanceDevices] ADD [ConnectionStatus] nvarchar(max) NOT NULL DEFAULT N'';

ALTER TABLE [AttendanceDevices] ADD [LastDownloadedCount] int NOT NULL DEFAULT 0;

ALTER TABLE [AttendanceDevices] ADD [LastSyncError] nvarchar(max) NULL;

ALTER TABLE [AttendanceDevices] ADD [Role] int NOT NULL DEFAULT 0;

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

CREATE INDEX [IX_SalaryAdvances_EmployeeId_Year_Month] ON [SalaryAdvances] ([EmployeeId], [Year], [Month]);

CREATE UNIQUE INDEX [IX_SalaryAdvances_PaymentRequestId] ON [SalaryAdvances] ([PaymentRequestId]) WHERE [PaymentRequestId] IS NOT NULL;

CREATE UNIQUE INDEX [IX_DeviceEmployeeMapping_AttendanceDeviceId_BiometricCode] ON [DeviceEmployeeMapping] ([AttendanceDeviceId], [BiometricCode]);

CREATE INDEX [IX_DeviceEmployeeMapping_EmployeeId] ON [DeviceEmployeeMapping] ([EmployeeId]);

CREATE INDEX [IX_DeviceSyncLog_AttendanceDeviceId_CreatedAt] ON [DeviceSyncLog] ([AttendanceDeviceId], [CreatedAt]);

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261117074343_TwoDeviceAndFlexibleAdvances', N'9.0.20');

COMMIT;
GO

