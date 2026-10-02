using System.Threading.RateLimiting;
using LazyDad.Api.Controllers;

namespace LazyDad.Api.Networking;

/// <summary>
/// Votes are anonymous, so at least cap how fast one address can cast them: <see cref="ConfigurationKey"/> votes a
/// minute per client address (<see cref="DefaultPerMinute"/> unless set). A load test raises it, since all its
/// simulated visitors come from one machine, so one address.
/// </summary>
public static class VoteRateLimit
{
    public const string ConfigurationKey = "RateLimiting:VotesPerMinute";
    public const int DefaultPerMinute = 30;

    public static IServiceCollection AddVoteRateLimit(this IServiceCollection services, IConfiguration configuration)
    {
        var perMinute = configuration.GetValue<int?>(ConfigurationKey) ?? DefaultPerMinute;
        if (perMinute < 1)
            throw new InvalidOperationException($"{ConfigurationKey} must be at least 1 (was {perMinute}).");

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(JokesController.VotePolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        return services;
    }
}
