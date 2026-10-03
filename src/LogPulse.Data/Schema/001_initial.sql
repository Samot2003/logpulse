-- Idempotent: safe to run on every startup.

IF OBJECT_ID(N'dbo.Servers', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Servers
    (
        Id           INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Servers PRIMARY KEY,
        Name         NVARCHAR(128)     NOT NULL,
        RegisteredAt DATETIMEOFFSET(3) NOT NULL,
        LastSeenAt   DATETIMEOFFSET(3) NOT NULL,
        CONSTRAINT UQ_Servers_Name UNIQUE (Name)
    );
END;

IF OBJECT_ID(N'dbo.LogEntries', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.LogEntries
    (
        Id        BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_LogEntries PRIMARY KEY,
        ServerId  INT                  NOT NULL CONSTRAINT FK_LogEntries_Servers REFERENCES dbo.Servers (Id),
        Timestamp DATETIMEOFFSET(3)    NOT NULL,
        Severity  TINYINT              NOT NULL,
        Source    NVARCHAR(256)        NOT NULL,
        Message   NVARCHAR(4000)       NOT NULL,
        Exception NVARCHAR(MAX)        NULL
    );

END;

-- Indexes are guarded one by one so a startup interrupted after CREATE TABLE still gets them.
-- Keys end in Id DESC to match ORDER BY Timestamp DESC, Id DESC without a sort.

-- Log viewer: newest first, optionally per server and severity.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_LogEntries_Server_Timestamp' AND object_id = OBJECT_ID(N'dbo.LogEntries'))
    CREATE INDEX IX_LogEntries_Server_Timestamp ON dbo.LogEntries (ServerId, Timestamp DESC, Id DESC) INCLUDE (Severity);

-- Log viewer across all servers, and retention purges by Timestamp.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_LogEntries_Timestamp' AND object_id = OBJECT_ID(N'dbo.LogEntries'))
    CREATE INDEX IX_LogEntries_Timestamp ON dbo.LogEntries (Timestamp DESC, Id DESC) INCLUDE (Severity, ServerId);

IF OBJECT_ID(N'dbo.MetricSamples', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MetricSamples
    (
        Id              BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_MetricSamples PRIMARY KEY,
        ServerId        INT                  NOT NULL CONSTRAINT FK_MetricSamples_Servers REFERENCES dbo.Servers (Id),
        Timestamp       DATETIMEOFFSET(3)    NOT NULL,
        CpuPercent      FLOAT                NOT NULL,
        MemoryUsedMb    BIGINT               NOT NULL,
        MemoryTotalMb   BIGINT               NOT NULL,
        DiskUsedPercent FLOAT                NOT NULL
    );
END;

-- Charts per server and the latest sample per server (TOP 1 seek).
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_MetricSamples_Server_Timestamp' AND object_id = OBJECT_ID(N'dbo.MetricSamples'))
    CREATE INDEX IX_MetricSamples_Server_Timestamp ON dbo.MetricSamples (ServerId, Timestamp DESC, Id DESC);

-- Retention purges by Timestamp.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_MetricSamples_Timestamp' AND object_id = OBJECT_ID(N'dbo.MetricSamples'))
    CREATE INDEX IX_MetricSamples_Timestamp ON dbo.MetricSamples (Timestamp);
