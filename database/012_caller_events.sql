-- Staging only. Apply after verifying the active staging database identity and backup.
-- Caller event metadata expires after ten minutes; never use this table for orders.
IF DB_NAME() <> N'anisedb'
    THROW 51000, 'CallerEvents migration requires the anisedb staging database.', 1;

IF OBJECT_ID(N'dbo.CallerEvents', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CallerEvents (
        Sequence bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_CallerEvents PRIMARY KEY,
        EventId uniqueidentifier NOT NULL,
        EventAtUtc datetimeoffset(0) NOT NULL,
        ReceivedAtUtc datetimeoffset(0) NOT NULL CONSTRAINT DF_CallerEvents_ReceivedAtUtc DEFAULT SYSDATETIMEOFFSET(),
        Phone nvarchar(21) NULL,
        CustomerId uniqueidentifier NULL,
        CustomerName nvarchar(160) NULL,
        CONSTRAINT UX_CallerEvents_EventId UNIQUE (EventId)
    );
    CREATE INDEX IX_CallerEvents_ReceivedAtUtc ON dbo.CallerEvents(ReceivedAtUtc);
END;
