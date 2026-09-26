using LazyDad.Data;
using Microsoft.Data.SqlClient;

namespace LazyDad.Tests;

public class SqlConnectionStringsTests
{
    [Fact]
    public void WithResumeTimeout_RaisesTheDefaultTimeout_AndKeepsTheRest()
    {
        var result = new SqlConnectionStringBuilder(SqlConnectionStrings.WithResumeTimeout(
            "Server=tcp:example.database.windows.net,1433;Database=lazydad-db-staging;User ID=app;Password=p;Encrypt=True"));

        Assert.Equal(SqlConnectionStrings.MinConnectTimeoutSeconds, result.ConnectTimeout);
        Assert.Equal("tcp:example.database.windows.net,1433", result.DataSource);
        Assert.Equal("lazydad-db-staging", result.InitialCatalog);
        Assert.Equal("app", result.UserID);
        Assert.Equal("p", result.Password);
    }

    [Fact]
    public void WithResumeTimeout_RaisesAShorterExplicitTimeout()
        => Assert.Equal(SqlConnectionStrings.MinConnectTimeoutSeconds,
            new SqlConnectionStringBuilder(SqlConnectionStrings.WithResumeTimeout("Server=s;Connect Timeout=30")).ConnectTimeout);

    [Fact]
    public void WithResumeTimeout_KeepsALongerTimeout()
        => Assert.Equal(120, new SqlConnectionStringBuilder(SqlConnectionStrings.WithResumeTimeout("Server=s;Connect Timeout=120")).ConnectTimeout);

    [Fact]
    public void WithResumeTimeout_AcceptsAnEmptyConnectionString()
        => Assert.Equal(SqlConnectionStrings.MinConnectTimeoutSeconds,
            new SqlConnectionStringBuilder(SqlConnectionStrings.WithResumeTimeout("")).ConnectTimeout);
}
