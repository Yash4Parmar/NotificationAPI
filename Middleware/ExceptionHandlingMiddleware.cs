using System.Text.Json;
using NotificationAPI.Models;
using NotificationAPI.Services;

namespace NotificationAPI.Middleware;

public sealed class ExceptionHandlingMiddleware
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;
    private readonly IHostEnvironment _environment;

    public ExceptionHandlingMiddleware(
        RequestDelegate next,
        ILogger<ExceptionHandlingMiddleware> logger,
        IHostEnvironment environment)
    {
        _next = next;
        _logger = logger;
        _environment = environment;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        if (context.Response.HasStarted)
        {
            _logger.LogError(exception, "An unhandled exception occurred after the response started.");
            throw exception;
        }

        var (statusCode, title, message) = MapException(exception);

        _logger.Log(
            statusCode >= StatusCodes.Status500InternalServerError ? LogLevel.Error : LogLevel.Warning,
            exception,
            "Request {TraceId} failed with {StatusCode}: {Message}",
            context.TraceIdentifier,
            statusCode,
            exception.Message);

        var errorResponse = new ErrorResponse
        {
            StatusCode = statusCode,
            Title = title,
            Message = message,
            TraceId = context.TraceIdentifier,
            Detail = _environment.IsDevelopment() ? exception.ToString() : null
        };

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";

        await context.Response.WriteAsync(JsonSerializer.Serialize(errorResponse, JsonOptions));
    }

    private static (int StatusCode, string Title, string Message) MapException(Exception exception)
    {
        return exception switch
        {
            GeminiApiException gemini => (
                (int)gemini.StatusCode,
                "Gemini API error",
                gemini.Message),

            HttpRequestException => (
                StatusCodes.Status502BadGateway,
                "Upstream request failed",
                "A dependent service could not be reached."),

            ArgumentException argument => (
                StatusCodes.Status400BadRequest,
                "Invalid request",
                argument.Message),

            InvalidOperationException invalidOperation => (
                StatusCodes.Status500InternalServerError,
                "Operation failed",
                invalidOperation.Message),

            _ => (
                StatusCodes.Status500InternalServerError,
                "Internal server error",
                "An unexpected error occurred.")
        };
    }
}
