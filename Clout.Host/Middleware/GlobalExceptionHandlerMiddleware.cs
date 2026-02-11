using System.Text.Json;
using Clout.Shared.Exceptions;
using Clout.Shared.Models;

namespace Clout.Host.Middleware;

/// <summary>
/// Middleware that catches unhandled exceptions and returns standardized error responses.
/// </summary>
public sealed class GlobalExceptionHandlerMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionHandlerMiddleware> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public GlobalExceptionHandlerMiddleware(RequestDelegate next, ILogger<GlobalExceptionHandlerMiddleware> logger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var correlationId = context.Items.ContainsKey("CorrelationId")
                ? context.Items["CorrelationId"]?.ToString() ?? string.Empty
                : context.TraceIdentifier;

            _logger.LogError(ex, "Unhandled exception occurred for request {Path}", context.Request.Path);
            await HandleExceptionAsync(context, ex, correlationId).ConfigureAwait(false);
        }
    }

    private static Task HandleExceptionAsync(HttpContext context, Exception exception, string correlationId)
    {
        context.Response.ContentType = "application/json";

        var (statusCode, errorCode, details) = GetErrorDetails(exception);
        context.Response.StatusCode = statusCode;

        var errorResponse = new ErrorResponse
        {
            Status = statusCode,
            ErrorCode = errorCode,
            Message = exception.Message,
            CorrelationId = correlationId,
            Timestamp = DateTime.UtcNow,
            Details = details
        };

        // Add validation errors if applicable
        if (exception is ValidationException validationEx)
        {
            errorResponse.Errors = validationEx.Errors;
        }

        var json = JsonSerializer.Serialize(errorResponse, JsonOptions);
        return context.Response.WriteAsync(json);
    }

    private static (int, string, Dictionary<string, object>?) GetErrorDetails(Exception exception) =>
        exception switch
        {
            BlobNotFoundException bex => (
                StatusCodes.Status404NotFound,
                "BLOB_NOT_FOUND",
                new Dictionary<string, object> { { "blobId", bex.BlobId } }
            ),
            BlobOperationException bex => (
                StatusCodes.Status500InternalServerError,
                bex.ErrorCode,
                new Dictionary<string, object> { { "blobId", bex.BlobId } }
            ),
            QueueQuotaExceededException qex => (
                StatusCodes.Status422UnprocessableEntity,
                qex.ErrorCode,
                new Dictionary<string, object> { { "queueName", qex.QueueName }, { "maxBytes", qex.MaxBytes } }
            ),
            QueueOperationException qex => (
                StatusCodes.Status500InternalServerError,
                qex.ErrorCode,
                new Dictionary<string, object> { { "queueName", qex.QueueName } }
            ),
            FunctionExecutionException fex => (
                StatusCodes.Status500InternalServerError,
                fex.ErrorCode,
                new Dictionary<string, object> { { "functionName", fex.FunctionName }, { "blobId", fex.BlobId } }
            ),
            ValidationException => (
                StatusCodes.Status400BadRequest,
                "VALIDATION_FAILED",
                null
            ),
            OperationCanceledException => (
                StatusCodes.Status408RequestTimeout,
                "OPERATION_CANCELLED",
                null
            ),
            _ => (
                StatusCodes.Status500InternalServerError,
                "INTERNAL_SERVER_ERROR",
                null
            )
        };
}

/// <summary>
/// Extension methods for global exception handler middleware.
/// </summary>
public static class GlobalExceptionHandlerMiddlewareExtensions
{
    /// <summary>
    /// Adds global exception handler middleware to the pipeline.
    /// </summary>
    public static IApplicationBuilder UseGlobalExceptionHandler(this IApplicationBuilder app)
    {
        return app.UseMiddleware<GlobalExceptionHandlerMiddleware>();
    }
}
