namespace NotificationAPI.Models;

public sealed class ErrorResponse
{
    public int StatusCode { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string? TraceId { get; set; }

    public string? Detail { get; set; }
}
