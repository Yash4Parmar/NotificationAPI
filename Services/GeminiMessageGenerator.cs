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
        You format system notifications into a single Discord channel message for an on-call engineering team.

        Severity emoji (exactly one at the start):
        - Warning → ⚠️
        - Error → ❌
        - Critical → 🚨

        Use this layout (fill in real values — never output curly braces or placeholder text):

        EMOJI **LEVEL — Category**

        **What happened:** one or two plain-language sentences from the notification message
        **Source:** source value, or Unknown if missing
        **Time (UTC):** timestamp from the payload
        **Action:** one concrete next step for on-call (based only on the message; do not invent details)

        Category: infer from the message (Storage, Network, Security, Database, Application, Performance, or Other).
        Use the Title for the header when it is useful; otherwise use the category.

        Discord formatting:
        - **bold** for field labels
        - `inline code` only for hostnames, paths, IDs, or metric values
        - blank line after the header line only

        Output rules:
        - Return ONLY the message body. No preamble, no explanation, no JSON, no code fences.
        - Keep under 1500 characters.
        - Do not repeat raw field names like "Level:" or "Message:" from the input.

        Example input:
        Level: Error
        Message: Disk usage on prod-db-01 reached 94%. Threshold is 90%.
        Title: High disk usage
        Source: monitoring/prometheus
        Timestamp: 2026-05-24 14:30:00 UTC

        Example output:
        ❌ **Error — Database**

        **What happened:** Disk usage on `prod-db-01` reached **94%**, above the **90%** threshold.
        **Source:** monitoring/prometheus
        **Time (UTC):** 2026-05-24 14:30:00 UTC
        **Action:** Check disk growth on `prod-db-01` and clear or expand storage before the volume fills.
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
                "Gemini API key is not configured. Set Gemini:ApiKey in appsettings.json.");

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
            Temperature = _options.Temperature,
            ThinkingConfig = new ThinkingConfig { ThinkingBudget = 0 }
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

                return SanitizeResponse(text);
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
        sb.AppendLine("Write a Discord-ready alert from this notification:");
        sb.AppendLine();
        sb.AppendLine($"Level: {request.Level}");
        sb.AppendLine($"Message: {request.Message}");
        sb.AppendLine($"Title: {request.Title ?? "N/A"}");
        sb.AppendLine($"Source: {request.Source ?? "N/A"}");
        sb.AppendLine($"Timestamp: {timestamp:yyyy-MM-dd HH:mm:ss} UTC");
        return sb.ToString();
    }

    private static string ExtractResponseText(Candidate? candidate)
    {
        if (candidate?.Content?.Parts is not { Count: > 0 } parts)
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            if (part.Thought == true || string.IsNullOrEmpty(part.Text))
                continue;

            sb.Append(part.Text);
        }

        return sb.ToString();
    }

    private static string SanitizeResponse(string text)
    {
        text = text.Trim();

        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0)
                text = text[(firstNewline + 1)..];

            if (text.EndsWith("```", StringComparison.Ordinal))
                text = text[..^3];

            text = text.Trim();
        }

        const string prefix = "Here";
        if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && text.Contains(':')
            && text.IndexOf('\n') is var lineBreak and > 0 and < 120)
        {
            text = text[(lineBreak + 1)..].Trim();
        }

        return text;
    }
}
