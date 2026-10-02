#:package Microsoft.Data.SqlClient@6.1.6

// Prepares the load-test database (Load Test Environment workflow): the app's database user, then synthetic jokes
// with votes and a Top 3, so the feed, its pages and the leaderboard look like production's. Run it after the
// migrations, signed in as the server's Entra admin (Active Directory Default: the workflow's Azure login).
//
//   dotnet run tests/load/Seed.cs -- <connection string> <jokes> <app identity name> <app identity client id>
//
// A database that already has jokes keeps them (a re-run of the set-up), so only the user is checked again.

using Microsoft.Data.SqlClient;

if (args.Length != 4 || !int.TryParse(args[1], out var jokes) || jokes < 1 || !Guid.TryParse(args[3], out var clientId))
{
    Console.Error.WriteLine("Usage: dotnet run tests/load/Seed.cs -- <connection string> <jokes (>= 1)> <app identity name> <app identity client id>");
    return 2;
}
var connectionString = args[0];
var appUser = args[2];

await using var connection = new SqlConnection(connectionString);
await connection.OpenAsync();

// The app signs in as its user-assigned identity. FROM EXTERNAL PROVIDER would make the server look it up in Microsoft
// Graph, which it may do with a person's sign-in (yours, in section 8) but not with this workflow's identity; WITH SID
// (the identity's client id) and TYPE = E need no lookup. QUOTENAME guards the name, the client id is a parsed Guid.
await ExecuteAsync("""
    IF DATABASE_PRINCIPAL_ID(@user) IS NULL
    BEGIN
        DECLARE @create nvarchar(400) = N'CREATE USER ' + QUOTENAME(@user) + N' WITH SID = '
            + CONVERT(nvarchar(34), CONVERT(varbinary(16), @clientId), 1) + N', TYPE = E';
        EXEC (@create);
    END
    DECLARE @reader nvarchar(400) = N'ALTER ROLE db_datareader ADD MEMBER ' + QUOTENAME(@user);
    DECLARE @writer nvarchar(400) = N'ALTER ROLE db_datawriter ADD MEMBER ' + QUOTENAME(@user);
    EXEC (@reader);
    EXEC (@writer);
    """, ("@user", appUser), ("@clientId", clientId));
Console.WriteLine($"Database user {appUser}: ready.");

await using (var count = new SqlCommand("SELECT COUNT(*) FROM Jokes", connection))
{
    var existing = (int)(await count.ExecuteScalarAsync())!;
    if (existing > 0)
    {
        Console.WriteLine($"The database already has {existing} jokes; not seeding again.");
        return 0;
    }
}

// Like production: two jokes per 4-hour tick (one per model), going back in time from now, texts of a real joke's
// length, and a few votes each. Then a Top 3, as the judge would have left it.
await ExecuteAsync("""
    WITH numbers AS (
        SELECT TOP (@jokes) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i
        FROM sys.all_objects a CROSS JOIN sys.all_objects b)
    INSERT INTO Jokes (Language, Model, Text, GeneratedAt, Up, Down)
    SELECT N'Ukrainian',
           CASE WHEN i % 2 = 0 THEN N'gpt-6-luna' ELSE N'Kimi-K2.5' END,
           CONCAT(N'Тестовий жарт ', i, N': тато взяв на збори гарбуз, бо чув, що там сьогодні будуть ділити гарбузи!'),
           DATEADD(minute, -((i + 1) / 2) * 240, SYSUTCDATETIME()),
           ABS(CHECKSUM(NEWID())) % 8,
           ABS(CHECKSUM(NEWID())) % 3
    FROM numbers;

    INSERT INTO TopJokes (Language, Rank, JokeId, Reason, JudgeModel, SelectedAt)
    SELECT TOP (3) N'Ukrainian', ROW_NUMBER() OVER (ORDER BY Up - Down DESC, Id), Id, N'Load test seed', N'gpt-6-sol', SYSUTCDATETIME()
    FROM Jokes
    ORDER BY Up - Down DESC, Id;
    """, ("@jokes", jokes));
Console.WriteLine($"Seeded {jokes} jokes and a Top 3.");
return 0;

async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
{
    await using var command = new SqlCommand(sql, connection) { CommandTimeout = 300 };
    foreach (var (name, value) in parameters)
        command.Parameters.AddWithValue(name, value);
    await command.ExecuteNonQueryAsync();
}
