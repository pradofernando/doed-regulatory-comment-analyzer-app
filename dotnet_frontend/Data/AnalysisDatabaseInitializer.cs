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
            await EnsureSqliteSessionNameColumnAsync(database, cancellationToken);
        }
        else if (database.Database.IsSqlServer())
        {
            await database.Database.ExecuteSqlRawAsync("""
                IF COL_LENGTH(N'Runs', N'SessionName') IS NULL
                BEGIN
                    ALTER TABLE [Runs] ADD [SessionName] nvarchar(160) NULL;
                END
                """, cancellationToken);
        }
    }

    private static async Task EnsureSqliteSessionNameColumnAsync(
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
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM pragma_table_info('Runs') WHERE name = 'SessionName';";
            var exists = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
            if (!exists)
            {
                await database.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"Runs\" ADD COLUMN \"SessionName\" TEXT NULL;",
                    cancellationToken);
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