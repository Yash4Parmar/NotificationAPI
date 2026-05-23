using System.Net;
using System.Text;
using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.Options;
using NotificationAPI.Configuration;
using NotificationAPI.Interfaces;
using NotificationAPI.Models;

namespace NotificationAPI.Services;

public class GeminiMessageGenerator : ILlmMessageGenerator, IDisposable
{
    private static readonly int[] RetryableStatusCodes =
    [
        (int)HttpStatusCode.TooManyRequests,
        (int)HttpStatusCode.ServiceUnavailable,
        (int)HttpStatusCode.GatewayTimeout
    ];

    private const string SystemPrompt = """
                                        You analyze incoming system notifications and write a single Discord message ready to post.

                                        From the notification payload:
                                        1) Determine the alert type (e.g. storage, network, security, database, application, performance).
                                        2) Use the provided severity level.
                                        3) Write a concise, actionable alert (2-4 sentences) for an on-call team.

                                        Output rules:
                                        - Return ONLY the Discord message text. No preamble, no explanation, no JSON.
                                        - Use Discord markdown: **bold**, `inline code`, and line breaks.
                                        - Start with one severity emoji: ⚠️ Warning, ❌ Error, 🚨 Critical.
                                        - Keep the message under 1800 characters.
                                        - Use this structure:

                                        {emoji} **{Severity} — {Type}**

                                        **Type:** {category}
                                        **Severity:** {level}
                                        **Source:** {source or Unknown}
                                        **Time:** {timestamp}

                                        {clear summary and recommended action}
                                        """;

    private readonly Client _client;
    private readonly GeminiOptions _options;
    private readonly ILogger<GeminiMessageGenerator> _logger;

    public GeminiMessageGenerator(
        IOptions<GeminiOptions> options,
        ILogger<GeminiMessageGenerator> logger)
    {
        _options = options.Value;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new InvalidOperationException(
                "Gemini API key is not configured. Set Gemini:ApiKey in appsettings or user secrets.");

        _client = new Client(apiKey: _options.ApiKey);
    }

    public async Task<string> GenerateMessageAsync(
        NotificationRequest request,
        CancellationToken cancellationToken = default)
    {
        var config = new GenerateContentConfig
        {
            SystemInstruction = new Content
            {
                Parts = [new Part { Text = SystemPrompt }]
            },
            MaxOutputTokens = _options.MaxTokens,
            Temperature = _options.Temperature
        };

        var maxAttempts = Math.Max(1, _options.MaxRetryAttempts);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var response = await _client.Models.GenerateContentAsync(
                    model: _options.Model,
                    contents: BuildNotificationText(request),
                    config: config,
                    cancellationToken: cancellationToken);

                var text = response.Candidates?[0].Content?.Parts?[0].Text;
                if (string.IsNullOrWhiteSpace(text))
                    throw new InvalidOperationException("Gemini returned an empty response.");

                return text;
            }
            catch (ClientError ex) when (RetryableStatusCodes.Contains(ex.StatusCode) && attempt < maxAttempts)
            {
                var delay = TimeSpan.FromSeconds(_options.RetryBaseDelaySeconds * Math.Pow(2, attempt - 1));
                _logger.LogWarning(
                    ex,
                    "Gemini returned {StatusCode} (attempt {Attempt}/{MaxAttempts}). Retrying in {DelaySeconds}s.",
                    ex.StatusCode,
                    attempt,
                    maxAttempts,
                    delay.TotalSeconds);

                await Task.Delay(delay, cancellationToken);
            }
            catch (ClientError ex)
            {
                var statusCode = (HttpStatusCode)ex.StatusCode;
                var message = ex.StatusCode == (int)HttpStatusCode.TooManyRequests
                    ? "Gemini API rate limit exceeded. Wait a few minutes or check quota in Google AI Studio."
                    : $"Gemini API request failed with status {ex.StatusCode} ({statusCode}).";

                throw new GeminiApiException(statusCode, ex.Message, message);
            }
            catch (ServerError ex) when (attempt < maxAttempts)
            {
                var delay = TimeSpan.FromSeconds(_options.RetryBaseDelaySeconds * Math.Pow(2, attempt - 1));
                _logger.LogWarning(
                    ex,
                    "Gemini server error (attempt {Attempt}/{MaxAttempts}). Retrying in {DelaySeconds}s.",
                    attempt,
                    maxAttempts,
                    delay.TotalSeconds);

                await Task.Delay(delay, cancellationToken);
            }
            catch (ServerError ex)
            {
                throw new GeminiApiException(
                    HttpStatusCode.ServiceUnavailable,
                    ex.Message,
                    "Gemini API server error. Try again later.");
            }
        }

        throw new InvalidOperationException("Gemini retry loop exited without a response.");
    }

    public void Dispose() => _client.Dispose();

    private static string BuildNotificationText(NotificationRequest request)
    {
        var timestamp = request.Timestamp ?? DateTime.UtcNow;
        var sb = new StringBuilder();
        sb.AppendLine($"Level: {request.Level}");
        sb.AppendLine($"Message: {request.Message}");
        sb.AppendLine($"Title: {request.Title ?? "N/A"}");
        sb.AppendLine($"Source: {request.Source ?? "N/A"}");
        sb.AppendLine($"Timestamp: {timestamp:O}");
        return sb.ToString();
    }
}
