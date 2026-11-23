-- REVIEW ONLY. Requires separate, explicit approval for a production SQL write.
-- Apply only after verifying the effective production database identity, the
-- read-only schema report, and a restorable production SQL backup.
-- This creates only the short-lived caller event table. It does not copy data.
SET XACT_ABORT ON;
IF DB_NAME() <> N'anisedatabase'
    THROW 51000, 'Expected production database anisedatabase.', 1;
IF OBJECT_ID(N'dbo.CallerEvents') IS NOT NULL
    THROW 51001, 'CallerEvents already exists; inspect its schema before migration.', 1;

BEGIN TRANSACTION;
CREATE TABLE dbo.CallerEvents (
    Sequence bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_CallerEvents PRIMARY KEY,
    EventId uniqueidentifier NOT NULL,
    CallId uniqueidentifier NOT NULL,
    CallStatus nvarchar(16) NOT NULL,
    EventAtUtc datetimeoffset(0) NOT NULL,
    ReceivedAtUtc datetimeoffset(0) NOT NULL
        CONSTRAINT DF_CallerEvents_ReceivedAtUtc DEFAULT SYSDATETIMEOFFSET(),
    Phone nvarchar(21) NULL,
    CustomerId uniqueidentifier NULL,
    CustomerName nvarchar(160) NULL,
    CONSTRAINT UX_CallerEvents_EventId UNIQUE (EventId)
);
CREATE INDEX IX_CallerEvents_ReceivedAtUtc ON dbo.CallerEvents(ReceivedAtUtc);
CREATE INDEX IX_CallerEvents_CallId_Sequence ON dbo.CallerEvents(CallId, Sequence DESC);
COMMIT TRANSACTION;
