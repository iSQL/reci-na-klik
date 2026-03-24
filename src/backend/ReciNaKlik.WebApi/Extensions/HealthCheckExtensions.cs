using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ReciNaKlik.WebApi.Extensions;

/// <summary>
/// Extension methods for registering and mapping health check endpoints with dependency verification.
/// </summary>
internal static class HealthCheckExtensions
{
    private const string ReadyTag = "ready";

    /// <summary>
    /// Registers health checks for application dependencies (PostgreSQL and optionally S3).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The application configuration for reading connection strings.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddApplicationHealthChecks(this IServiceCollection services,
        IConfiguration configuration)
    {
        var healthChecks = services.AddHealthChecks();

        var connectionString = configuration.GetConnectionString("Database")
                               ?? throw new InvalidOperationException(
                                   "ConnectionStrings:Database is required for the health check.");

        healthChecks.AddNpgSql(
            connectionString,
            name: "PostgreSQL",
            timeout: TimeSpan.FromSeconds(3),
            tags: [ReadyTag]);


        return services;
    }

    /// <summary>
    /// Maps health check endpoints with rate limiting disabled:
    /// <list type="bullet">
    ///   <item><c>/health</c> - all checks, JSON response</item>
    ///   <item><c>/health/ready</c> - readiness checks only (DB), JSON response</item>
    ///   <item><c>/health/live</c> - no checks, always 200 Healthy, plain text</item>
    /// </list>
    /// </summary>
    /// <param name="app">The web application.</param>
    /// <returns>The web application for chaining.</returns>
    public static WebApplication MapHealthCheckEndpoints(this WebApplication app)
    {
        app.MapHealthChecks("/health", new()
            {
                ResponseWriter = WriteJsonResponse
            })
            .DisableRateLimiting();

        app.MapHealthChecks("/health/ready", new()
            {
                Predicate = check => check.Tags.Contains(ReadyTag),
                ResponseWriter = WriteJsonResponse
            })
            .DisableRateLimiting();

        app.MapHealthChecks("/health/live", new()
            {
                Predicate = _ => false
            })
            .DisableRateLimiting();

        return app;
    }


    /// <summary>
    /// Writes a JSON health check response with per-check details.
    /// </summary>
    private static async Task WriteJsonResponse(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        var response = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                description = entry.Value.Description,
                duration = entry.Value.Duration.ToString()
            }),
            totalDuration = report.TotalDuration.ToString()
        };

        await context.Response.WriteAsJsonAsync(response);
    }
}
