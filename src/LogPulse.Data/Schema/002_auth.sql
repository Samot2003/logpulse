-- Idempotent: safe to run on every startup.

IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Users
    (
        Id           INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Users PRIMARY KEY,
        UserName     NVARCHAR(64)      NOT NULL CONSTRAINT UQ_Users_UserName UNIQUE,
        PasswordHash NVARCHAR(512)     NOT NULL,
        Role         NVARCHAR(16)      NOT NULL CONSTRAINT CK_Users_Role CHECK (Role IN (N'Viewer', N'Admin')),
        CreatedAt    DATETIMEOFFSET(3) NOT NULL
    );
END;

-- API keys are high-entropy random values, so a plain SHA-256 hash is enough (no slow KDF needed).
IF OBJECT_ID(N'dbo.AgentCredentials', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AgentCredentials
    (
        Id         INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AgentCredentials PRIMARY KEY,
        ServerName NVARCHAR(128)     NOT NULL CONSTRAINT UQ_AgentCredentials_ServerName UNIQUE,
        KeyHash    BINARY(32)        NOT NULL,
        -- Incremented on every key rotation; sessions remember the version they were opened with.
        KeyVersion INT               NOT NULL CONSTRAINT DF_AgentCredentials_KeyVersion DEFAULT (1),
        CreatedAt  DATETIMEOFFSET(3) NOT NULL,
        RevokedAt  DATETIMEOFFSET(3) NULL
    );
END;

IF OBJECT_ID(N'dbo.RefreshTokens', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.RefreshTokens
    (
        Id          BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_RefreshTokens PRIMARY KEY,
        TokenHash   BINARY(32)           NOT NULL CONSTRAINT UQ_RefreshTokens_TokenHash UNIQUE,
        FamilyId    UNIQUEIDENTIFIER     NOT NULL,
        SubjectType TINYINT              NOT NULL,
        SubjectId   INT                  NOT NULL,
        -- Credential version the session was opened with (agents: AgentCredentials.KeyVersion; users: 0).
        SubjectVersion INT               NOT NULL CONSTRAINT DF_RefreshTokens_SubjectVersion DEFAULT (0),
        -- When the session (the first token of the family) started; caps the session's absolute lifetime.
        FamilyCreatedAt DATETIMEOFFSET(3) NOT NULL,
        CreatedAt   DATETIMEOFFSET(3)    NOT NULL,
        ExpiresAt   DATETIMEOFFSET(3)    NOT NULL,
        UsedAt      DATETIMEOFFSET(3)    NULL,
        RevokedAt   DATETIMEOFFSET(3)    NULL
    );
END;

-- Revoking a family, and the "family already revoked?" guard when inserting a rotated token.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RefreshTokens_FamilyId' AND object_id = OBJECT_ID(N'dbo.RefreshTokens'))
    CREATE INDEX IX_RefreshTokens_FamilyId ON dbo.RefreshTokens (FamilyId) INCLUDE (RevokedAt);

-- Revoking every session of a user or agent (e.g. when an agent key is rotated or revoked).
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RefreshTokens_Subject' AND object_id = OBJECT_ID(N'dbo.RefreshTokens'))
    CREATE INDEX IX_RefreshTokens_Subject ON dbo.RefreshTokens (SubjectType, SubjectId) INCLUDE (RevokedAt);

-- Purging expired tokens.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RefreshTokens_ExpiresAt' AND object_id = OBJECT_ID(N'dbo.RefreshTokens'))
    CREATE INDEX IX_RefreshTokens_ExpiresAt ON dbo.RefreshTokens (ExpiresAt);
