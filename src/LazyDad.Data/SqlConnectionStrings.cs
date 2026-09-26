using Microsoft.Data.SqlClient;

namespace LazyDad.Data;

public static class SqlConnectionStrings
{
    /// <summary>
    /// The shortest connect timeout the app uses. A paused serverless database (staging) takes up to
    /// about a minute to resume, and the login waits for it: with the 15 s default, the login times out
    /// ("post-login phase"). EF's retry strategy doesn't retry that timeout, since the same error number
    /// also covers command timeouts, where a retried insert could duplicate a joke.
    /// </summary>
    public const int MinConnectTimeoutSeconds = 60;

    /// <summary>
    /// Returns <paramref name="connectionString"/> with a connect timeout of at least
    /// <see cref="MinConnectTimeoutSeconds"/>; a longer one that's already set is kept, and so is 0
    /// (SqlClient's "wait indefinitely").
    /// </summary>
    public static string WithResumeTimeout(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (builder.ConnectTimeout > 0 && builder.ConnectTimeout < MinConnectTimeoutSeconds)
            builder.ConnectTimeout = MinConnectTimeoutSeconds;
        return builder.ConnectionString;
    }
}
