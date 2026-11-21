IF DB_NAME() <> N'anisedb'
    THROW 51000, 'Caller lifecycle migration requires the anisedb staging database.', 1;

IF OBJECT_ID(N'dbo.CallerEvents', N'U') IS NULL
    THROW 51001, 'CallerEvents must exist before the lifecycle migration.', 1;

IF COL_LENGTH(N'dbo.CallerEvents', N'CallId') IS NULL
    ALTER TABLE dbo.CallerEvents ADD CallId uniqueidentifier NULL;
IF COL_LENGTH(N'dbo.CallerEvents', N'CallStatus') IS NULL
    ALTER TABLE dbo.CallerEvents ADD CallStatus nvarchar(16) NULL;

UPDATE dbo.CallerEvents SET CallId = EventId, CallStatus = N'ringing'
WHERE CallId IS NULL OR CallStatus IS NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CallerEvents') AND name = N'IX_CallerEvents_CallId_Sequence')
    CREATE INDEX IX_CallerEvents_CallId_Sequence ON dbo.CallerEvents(CallId, Sequence DESC);
