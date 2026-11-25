-- =============================================================================
-- On-Premises NAS File Monitor - SQL Server State Ledger & HA Lease DDL
-- =============================================================================

CREATE TABLE dbo.TrackedFiles (
    TrackedFileId        BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_TrackedFiles PRIMARY KEY,
    RuleId               NVARCHAR(128)        NOT NULL,
    RelativePath         NVARCHAR(512)        NOT NULL,
    FileName             NVARCHAR(260)        NOT NULL,
    FileSizeBytes        BIGINT               NOT NULL,
    LastWriteTimeUtc     DATETIME2(7)         NOT NULL,
    AttemptNumber        INT                  NOT NULL CONSTRAINT DF_TrackedFiles_Attempt DEFAULT (1),
    StabilityHits        INT                  NOT NULL CONSTRAINT DF_TrackedFiles_Stability DEFAULT (1),
    State                NVARCHAR(32)         NOT NULL,
    FirstSeenUtc         DATETIME2(3)         NOT NULL,
    LastSeenUtc          DATETIME2(3)         NOT NULL,
    VerifiedAtUtc        DATETIME2(3)         NULL,
    FolderVerifiedAtUtc  DATETIME2(3)         NULL,
    MatchedTargetFile    NVARCHAR(512)        NULL,
    DbStatus             NVARCHAR(64)         NULL,
    AlertedAtUtc         DATETIME2(3)         NULL
);
GO

-- Prevents duplicate rows for the same file generation while allowing re-drops with new size/timestamp
CREATE UNIQUE INDEX UX_TrackedFiles_Identity
    ON dbo.TrackedFiles (RuleId, RelativePath, FileSizeBytes, LastWriteTimeUtc);
GO

-- Fast lookup for active non-terminal files per rule on every polling tick
CREATE INDEX IX_TrackedFiles_ActiveByRule
    ON dbo.TrackedFiles (RuleId, State)
    INCLUDE (RelativePath, FileName, FileSizeBytes, LastWriteTimeUtc, FirstSeenUtc, MatchedTargetFile, DbStatus, AlertedAtUtc)
    WHERE State NOT IN ('BaselineIgnored', 'Succeeded', 'SucceededFastPath', 'Failed');
GO

-- Fast lookup for Target-Side Reverse Discovery deduplication
CREATE INDEX IX_TrackedFiles_MatchedTarget
    ON dbo.TrackedFiles (RuleId, MatchedTargetFile)
    WHERE MatchedTargetFile IS NOT NULL;
GO

CREATE TABLE dbo.RuleHeartbeats (
    RuleId                NVARCHAR(128) NOT NULL CONSTRAINT PK_RuleHeartbeats PRIMARY KEY,
    LastTickStartedUtc    DATETIME2(3)  NOT NULL,
    LastTickCompletedUtc  DATETIME2(3)  NOT NULL,
    RuleHealth            NVARCHAR(32)  NOT NULL,
    PendingCount          INT           NOT NULL
);
GO

CREATE TABLE dbo.ServiceLeases (
    LeaseName         NVARCHAR(128) NOT NULL CONSTRAINT PK_ServiceLeases PRIMARY KEY,
    OwnerMachineName  NVARCHAR(128) NOT NULL,
    AcquiredUtc       DATETIME2(3)  NOT NULL,
    ExpiresUtc        DATETIME2(3)  NOT NULL
);
GO

-- Nightly retention procedure: purges old Moved/temporary rows while preserving permanent-file keys
CREATE OR ALTER PROCEDURE dbo.usp_PurgeOldTrackedFiles
    @RetentionDays INT = 60
AS
BEGIN
    SET NOCOUNT ON;

    DELETE TOP (10000) FROM dbo.TrackedFiles
    WHERE State IN ('Succeeded', 'SucceededFastPath', 'Failed')
      AND VerifiedAtUtc < DATEADD(DAY, -@RetentionDays, SYSUTCDATETIME())
      AND RuleId NOT IN (
          -- Keep terminal index rows for rules whose files stay in SourcePath permanently
          'correspondence-stays-in-place'
      );
END;
GO
