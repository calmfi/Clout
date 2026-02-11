using System.Diagnostics;

namespace Clout.Host.Middleware;

/// <summary>
/// Middleware that adds correlation IDs to requests and logs request/response details.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    private const string CorrelationIdHeader = "X-Correlation-ID";
    private const string TraceIdHeader = "X-Trace-ID";
    private const string CorrelationIdProperty = "CorrelationId";

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = GetOrCreateCorrelationId(context);
        context.Items[CorrelationIdProperty] = correlationId;

        // Add correlation ID to response headers
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[CorrelationIdHeader] = correlationId;
            context.Response.Headers[TraceIdHeader] = Activity.Current?.Id ?? context.TraceIdentifier;
            return Task.CompletedTask;
        });

        using (_logger.BeginScope(new Dictionary<string, object> { { CorrelationIdProperty, correlationId } }))
        {
            var startTime = Stopwatch.StartNew();
            
            try
            {
                _logger.LogInformation(
                    "Request started: {Method} {Path}",
                    context.Request.Method,
                    context.Request.Path);

                await _next(context).ConfigureAwait(false);

                startTime.Stop();
                _logger.LogInformation(
                    "Request completed: {Method} {Path} - Status: {StatusCode} - Duration: {Duration}ms",
                    context.Request.Method,
                    context.Request.Path,
                    context.Response.StatusCode,
                    startTime.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                startTime.Stop();
                _logger.LogError(
                    ex,
                    "Request failed: {Method} {Path} - Duration: {Duration}ms",
                    context.Request.Method,
                    context.Request.Path,
                    startTime.ElapsedMilliseconds);
                throw;
            }
        }
    }

    private static string GetOrCreateCorrelationId(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(CorrelationIdHeader, out var correlationId) &&
            !string.IsNullOrWhiteSpace(correlationId.ToString()))
        {
            return correlationId.ToString();
        }

        return Guid.NewGuid().ToString("N");
    }
}

/// <summary>
/// Extension methods for correlation ID middleware.
/// </summary>
public static class CorrelationIdMiddlewareExtensions
{
    /// <summary>
    /// Adds correlation ID middleware to the pipeline.
    /// </summary>
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app)
    {
        return app.UseMiddleware<CorrelationIdMiddleware>();
    }
}
