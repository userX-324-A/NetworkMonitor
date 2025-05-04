IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20250501194437_InitialCreateSqlServer'
)
BEGIN
    CREATE TABLE [NetworkUsageLogs] (
        [Id] int NOT NULL IDENTITY,
        [Timestamp] datetime2 NOT NULL,
        [ProcessId] int NOT NULL,
        [ProcessName] nvarchar(max) NULL,
        [BytesSent] bigint NOT NULL,
        [BytesReceived] bigint NOT NULL,
        CONSTRAINT [PK_NetworkUsageLogs] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20250501194437_InitialCreateSqlServer'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20250501194437_InitialCreateSqlServer', N'8.0.13');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20250501203538_AddTopRemoteIp'
)
BEGIN
    ALTER TABLE [NetworkUsageLogs] ADD [TopRemoteIpAddress] nvarchar(max) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20250501203538_AddTopRemoteIp'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20250501203538_AddTopRemoteIp', N'8.0.13');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20250503000406_AddDiskIOColumns'
)
BEGIN
    ALTER TABLE [NetworkUsageLogs] ADD [BytesRead] bigint NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20250503000406_AddDiskIOColumns'
)
BEGIN
    ALTER TABLE [NetworkUsageLogs] ADD [BytesWritten] bigint NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20250503000406_AddDiskIOColumns'
)
BEGIN
    CREATE TABLE [DiskActivityLogs] (
        [Id] int NOT NULL IDENTITY,
        [Timestamp] datetime2 NOT NULL,
        [ProcessId] int NOT NULL,
        [ProcessName] nvarchar(max) NULL,
        [BytesRead] bigint NOT NULL,
        [BytesWritten] bigint NOT NULL,
        [ReadOperationCount] bigint NOT NULL,
        [WriteOperationCount] bigint NOT NULL,
        [TopReadFile] nvarchar(max) NULL,
        [TopWriteFile] nvarchar(max) NULL,
        [TopOtherFile] nvarchar(max) NULL,
        [OtherOperationCount] bigint NOT NULL,
        CONSTRAINT [PK_DiskActivityLogs] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20250503000406_AddDiskIOColumns'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20250503000406_AddDiskIOColumns', N'8.0.13');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20250503002000_RemoveDiskColumnsFromNetworkUsage'
)
BEGIN
    DECLARE @var0 sysname;
    SELECT @var0 = [d].[name]
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[NetworkUsageLogs]') AND [c].[name] = N'BytesRead');
    IF @var0 IS NOT NULL EXEC(N'ALTER TABLE [NetworkUsageLogs] DROP CONSTRAINT [' + @var0 + '];');
    ALTER TABLE [NetworkUsageLogs] DROP COLUMN [BytesRead];
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20250503002000_RemoveDiskColumnsFromNetworkUsage'
)
BEGIN
    DECLARE @var1 sysname;
    SELECT @var1 = [d].[name]
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[NetworkUsageLogs]') AND [c].[name] = N'BytesWritten');
    IF @var1 IS NOT NULL EXEC(N'ALTER TABLE [NetworkUsageLogs] DROP CONSTRAINT [' + @var1 + '];');
    ALTER TABLE [NetworkUsageLogs] DROP COLUMN [BytesWritten];
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20250503002000_RemoveDiskColumnsFromNetworkUsage'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20250503002000_RemoveDiskColumnsFromNetworkUsage', N'8.0.13');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20250503010855_RemoveOtherDiskColumns'
)
BEGIN
    DECLARE @var2 sysname;
    SELECT @var2 = [d].[name]
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[DiskActivityLogs]') AND [c].[name] = N'OtherOperationCount');
    IF @var2 IS NOT NULL EXEC(N'ALTER TABLE [DiskActivityLogs] DROP CONSTRAINT [' + @var2 + '];');
    ALTER TABLE [DiskActivityLogs] DROP COLUMN [OtherOperationCount];
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20250503010855_RemoveOtherDiskColumns'
)
BEGIN
    DECLARE @var3 sysname;
    SELECT @var3 = [d].[name]
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[DiskActivityLogs]') AND [c].[name] = N'TopOtherFile');
    IF @var3 IS NOT NULL EXEC(N'ALTER TABLE [DiskActivityLogs] DROP CONSTRAINT [' + @var3 + '];');
    ALTER TABLE [DiskActivityLogs] DROP COLUMN [TopOtherFile];
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20250503010855_RemoveOtherDiskColumns'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20250503010855_RemoveOtherDiskColumns', N'8.0.13');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20250503022756_SomeChange'
)
BEGIN
    EXEC sp_rename N'[DiskActivityLogs].[WriteOperationCount]', N'WriteOperations', N'COLUMN';
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20250503022756_SomeChange'
)
BEGIN
    EXEC sp_rename N'[DiskActivityLogs].[ReadOperationCount]', N'ReadOperations', N'COLUMN';
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20250503022756_SomeChange'
)
BEGIN
    DECLARE @var4 sysname;
    SELECT @var4 = [d].[name]
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[NetworkUsageLogs]') AND [c].[name] = N'ProcessName');
    IF @var4 IS NOT NULL EXEC(N'ALTER TABLE [NetworkUsageLogs] DROP CONSTRAINT [' + @var4 + '];');
    EXEC(N'UPDATE [NetworkUsageLogs] SET [ProcessName] = N'''' WHERE [ProcessName] IS NULL');
    ALTER TABLE [NetworkUsageLogs] ALTER COLUMN [ProcessName] nvarchar(max) NOT NULL;
    ALTER TABLE [NetworkUsageLogs] ADD DEFAULT N'' FOR [ProcessName];
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20250503022756_SomeChange'
)
BEGIN
    DECLARE @var5 sysname;
    SELECT @var5 = [d].[name]
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[DiskActivityLogs]') AND [c].[name] = N'ProcessName');
    IF @var5 IS NOT NULL EXEC(N'ALTER TABLE [DiskActivityLogs] DROP CONSTRAINT [' + @var5 + '];');
    EXEC(N'UPDATE [DiskActivityLogs] SET [ProcessName] = N'''' WHERE [ProcessName] IS NULL');
    ALTER TABLE [DiskActivityLogs] ALTER COLUMN [ProcessName] nvarchar(max) NOT NULL;
    ALTER TABLE [DiskActivityLogs] ADD DEFAULT N'' FOR [ProcessName];
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20250503022756_SomeChange'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20250503022756_SomeChange', N'8.0.13');
END;
GO

COMMIT;
GO

