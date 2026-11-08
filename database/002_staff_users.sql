/* Run after 001_initial_schema.sql. SQL Server 2019 / compatibility level 150. */
SET XACT_ABORT ON;
BEGIN TRANSACTION;

CREATE TABLE dbo.StaffUsers (
    Id uniqueidentifier NOT NULL PRIMARY KEY,
    CreatedAtUtc datetimeoffset(0) NOT NULL,
    UpdatedAtUtc datetimeoffset(0) NULL,
    Email nvarchar(254) NOT NULL,
    DisplayName nvarchar(100) NOT NULL,
    PasswordHash nvarchar(500) NOT NULL,
    Role int NOT NULL,
    IsActive bit NOT NULL DEFAULT 1,
    LastSignedInAtUtc datetimeoffset(0) NULL,
    CONSTRAINT UQ_StaffUsers_Email UNIQUE (Email)
);

COMMIT TRANSACTION;
