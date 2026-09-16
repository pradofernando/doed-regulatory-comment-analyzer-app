using System.Data;
using Microsoft.EntityFrameworkCore;

namespace DoedRegulatoryComments.Web.Data;

public static class AnalysisDatabaseInitializer
{
    public static async Task InitializeAsync(
        IDbContextFactory<AnalysisDbContext> factory,
        CancellationToken cancellationToken = default)
    {
        await using var database = await factory.CreateDbContextAsync(cancellationToken);
        await database.Database.EnsureCreatedAsync(cancellationToken);

        if (database.Database.IsSqlite())
        {
            await EnsureSqliteColumnsAsync(database, cancellationToken);
            await database.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS "WorkspaceDocuments" (
                    "Kind" TEXT COLLATE BINARY NOT NULL,
                    "Id" TEXT COLLATE BINARY NOT NULL,
                    "Json" TEXT NOT NULL,
                    "Version" TEXT COLLATE BINARY NOT NULL,
                    CONSTRAINT "PK_WorkspaceDocuments" PRIMARY KEY ("Kind", "Id")
                );
                """, cancellationToken);
        }
        else if (database.Database.IsSqlServer())
        {
            await database.Database.ExecuteSqlRawAsync("""
                IF COL_LENGTH(N'Runs', N'SessionName') IS NULL
                BEGIN
                    ALTER TABLE [Runs] ADD [SessionName] nvarchar(160) NULL;
                END
                IF COL_LENGTH(N'Runs', N'SourcesJson') IS NULL
                BEGIN
                    ALTER TABLE [Runs] ADD [SourcesJson] nvarchar(max) NOT NULL DEFAULT N'[]';
                END
                IF COL_LENGTH(N'Runs', N'ProvenanceJson') IS NULL
                BEGIN
                    ALTER TABLE [Runs] ADD [ProvenanceJson] nvarchar(max) NULL;
                END
                IF OBJECT_ID(N'Categorizations', N'U') IS NOT NULL
                   AND COL_LENGTH(N'Categorizations', N'RowData') IS NULL
                BEGIN
                    ALTER TABLE [Categorizations] ADD [RowData] nvarchar(max) NOT NULL DEFAULT N'';
                END
                IF OBJECT_ID(N'ThemeGroups', N'U') IS NOT NULL
                   AND COL_LENGTH(N'ThemeGroups', N'EvidenceJson') IS NULL
                BEGIN
                    ALTER TABLE [ThemeGroups] ADD [EvidenceJson] nvarchar(max) NOT NULL DEFAULT N'[]';
                END
                IF OBJECT_ID(N'WorkspaceDocuments', N'U') IS NULL
                BEGIN
                    CREATE TABLE [WorkspaceDocuments] (
                        [Kind] nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
                        [Id] nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
                        [Json] nvarchar(max) NOT NULL,
                        [Version] nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
                        CONSTRAINT [PK_WorkspaceDocuments] PRIMARY KEY ([Kind], [Id])
                    );
                END
                """, cancellationToken);
        }
    }

    private static async Task EnsureSqliteColumnsAsync(
        AnalysisDbContext database,
        CancellationToken cancellationToken)
    {
        var connection = database.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            var additions = new[]
            {
                ("Runs", "SessionName", "TEXT NULL"),
                ("Runs", "SourcesJson", "TEXT NOT NULL DEFAULT '[]'"),
                ("Runs", "ProvenanceJson", "TEXT NULL"),
                ("Categorizations", "RowData", "TEXT NOT NULL DEFAULT ''"),
                ("ThemeGroups", "EvidenceJson", "TEXT NOT NULL DEFAULT '[]'"),
            };
            foreach (var (table, column, definition) in additions)
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"SELECT COUNT(*) FROM pragma_table_info('{table}');";
                if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 0)
                    continue;

                command.CommandText =
                    $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}';";
                if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 0)
                {
                    command.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition};";
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }
            }
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync();
            }
        }
    }
}