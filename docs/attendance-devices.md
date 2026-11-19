# Attendance device integration

The manual import is functional independently of devices. `/AttendanceDevices` saves device
configuration; there is deliberately no registered network driver and no attendance download action.
An unsupported model reports that no connection was attempted.

## Database review

Review `src/HrPayroll.Infrastructure/Migrations/20261116120000_AttendanceImportDeviceSupport.cs`
before deployment. Its `Up` operation only:

- Creates `AttendanceDevices` for configuration and synchronization status.
- Adds nullable `RawPunches.OriginalFieldsJson` to retain unclassified original source columns.
- Adds `ImportBatches.ReviewRows`, initialized to zero for historical batches.

No employee, payroll, attendance, Identity, existing migration, or historical hash is updated or
deleted. The change does not add uniqueness constraints to existing data. The migration remains
unapplied by this implementation. Automatic startup migrations are disabled by default and are
always skipped outside Development. Apply only after your normal backup and migration review.
The migration intentionally refuses an automatic rollback that would discard configuration and
diagnostic data.

## Adding a real driver

1. Obtain the exact model, firmware, documented protocol or compatible official SDK, serial number,
   port, and communication-key requirements. Do not infer compatibility from the ZKTeco brand.
2. Implement `IZkDeviceClient`; `Supports` must identify models the implementation has verified.
   Register the implementation in dependency injection. Multiple matching drivers are rejected.
3. Return documented explicit attendance directions, biometric codes as strings, original fields,
   and local wall-clock timestamps with `DateTimeKind.Unspecified`. Do not infer metadata columns.
   Match the configured device timezone with the company's attendance timezone before enabling
   synchronization; the current bridge preserves device local time and does not convert zones.
4. Use `AttendanceDeviceService.DownloadForPreviewAsync` for the same normalized employee matching,
   direction validation and duplicate analysis as manual files. After an authorized preview confirmation,
   call `AttendanceImportService.CommitAsync` and the existing attendance-processing flow. Update
   `LastSuccessfulSyncUtc` only after a completed synchronization; it must never be set by configuration
   save or a connection test. A future sync workflow should record its batch ID and failure state.
5. Verify SDK bitness against Windows and the IIS worker process, native library deployment,
   connection lifetime/disposal, bounded timeouts, cancellation, concurrent access, and failure recovery.
   The service passes cancellation and bounded timeouts; a native driver must honor those safely.
6. Verify LAN reachability from the application server or deploy a local synchronization service that
   uses the same pipeline. Local device communication must work without internet access.
7. Never erase device attendance logs as part of downloading or committing records.

Communication keys are protected with ASP.NET Core Data Protection and are never returned to the
configuration form or audit payload. Configure persistent, access-controlled Data Protection keys
for the deployment so stored communication keys remain readable after app-pool recycling or redeployment.
Do not put communication keys in application logs or repository configuration files.

`ZkDeviceTests` uses a sanitized in-memory fixture driver solely to verify cross-source deduplication.
It provides no claim of compatibility or connectivity with a real device.
