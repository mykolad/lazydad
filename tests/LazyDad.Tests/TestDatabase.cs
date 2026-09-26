using LazyDad.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LazyDad.Tests;

/// <summary>
/// The database behind the repository tests. By default SQLite in memory, with the schema from
/// <c>EnsureCreated()</c>: fast and needs nothing installed. When <see cref="SqlServerVariable"/> is set (the CI
/// "clean database migrations" job), a new SQL Server database with the <b>real migrations</b> applied, so the same
/// tests also run against production's provider and schema. It's dropped again on dispose.
/// </summary>
public sealed class TestDatabase : IDisposable
{
    public const string SqlServerVariable = "SQLSERVER_TEST_CONNECTION";

    private readonly SqliteConnection? sqlite;
    private readonly string? sqlServer;

    public TestDatabase()
    {
        var server = Environment.GetEnvironmentVariable(SqlServerVariable);
        if (string.IsNullOrWhiteSpace(server))
        {
            sqlite = new SqliteConnection("DataSource=:memory:");
            sqlite.Open();
            using var context = CreateContext();
            context.Database.EnsureCreated();
        }
        else
        {
            sqlServer = new SqlConnectionStringBuilder(server) { InitialCatalog = $"lazydad_test_{Guid.NewGuid():N}" }.ConnectionString;
            using var context = CreateContext();
            context.Database.Migrate();
        }
    }

    public LazyDadDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<LazyDadDbContext>();
        if (sqlite is not null)
            options.UseSqlite(sqlite);
        else
            options.UseSqlServer(sqlServer);
        return new LazyDadDbContext(options.Options);
    }

    public void Dispose()
    {
        if (sqlServer is not null)
        {
            using var context = CreateContext();
            context.Database.EnsureDeleted();
        }
        sqlite?.Dispose();
    }
}
