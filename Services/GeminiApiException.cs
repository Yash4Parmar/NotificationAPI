namespace NotificationAPI.Services;

public sealed class GeminiApiException : Exception
{
    public GeminiApiException(
        System.Net.HttpStatusCode statusCode,
        string? responseBody,
        string message)
        : base(message)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public System.Net.HttpStatusCode StatusCode { get; }

    public string? ResponseBody { get; }
}
