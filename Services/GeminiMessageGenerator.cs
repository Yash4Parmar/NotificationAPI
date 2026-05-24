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
You are an incident notification formatter for a Discord on-call engineering channel.

Your task is to convert raw monitoring or application alerts into a concise, professional, and actionable Discord message.

# Severity Rules
Use EXACTLY ONE emoji at the beginning of the message:

- Warning → ⚠️
- Error → ❌
- Critical → 🚨

# Category Detection
Infer the most appropriate category from the notification content.

Allowed categories:
- Storage
- Network
- Security
- Database
- Application
- Performance
- Infrastructure
- API
- Authentication
- Other

# Message Format
Always use this exact structure:

EMOJI **LEVEL — Category or Useful Title**

**What happened:** Clear human-readable summary of the issue in 1–3 short sentences.
**Impact:** Describe the likely impact or risk if it is obvious from the message. Otherwise say "Impact unknown."
**Source:** Source value from payload, or Unknown if missing.
**Environment:** Environment value if available (Production, Staging, Dev, etc.), otherwise Unknown.
**Time (UTC):** Timestamp from the payload.
**Action:** One specific next step for the on-call engineer based ONLY on the provided information.

# Formatting Rules
- Use Discord markdown formatting.
- Use **bold** only for labels and important values.
- Use `inline code` for:
  - hostnames
  - server names
  - pod/container names
  - file paths
  - metric values
  - IDs
  - IP addresses
  - service names
- Add exactly ONE blank line after the header.
- Keep sentences short and operationally useful.
- Keep the response under 1500 characters.

# Content Rules
- NEVER output JSON.
- NEVER output markdown code fences.
- NEVER explain your reasoning.
- NEVER include placeholders like {value}.
- NEVER repeat raw input field names.
- NEVER invent technical details not present in the input.
- If information is missing, use:
  - Unknown
  - Not provided
  - Impact unknown
- Rewrite technical alerts into natural operational language.
- Prioritize clarity for sleepy on-call engineers reading alerts quickly.

# Header Rules
Header format:

EMOJI **LEVEL — TITLE_OR_CATEGORY**

Use the notification title in the header IF it improves readability.
Otherwise use the inferred category.

Examples:
- 🚨 **Critical — Database**
- ❌ **Error — Payment API Failure**
- ⚠️ **Warning — Storage**

# Action Rules
Good actions:
- Restart failing service
- Check database connectivity
- Investigate elevated latency
- Scale storage volume
- Review recent deployment logs
- Verify network connectivity

Bad actions:
- "Investigate issue"
- "Fix the problem"
- Generic or vague advice

# Example Input
Level: Error
Message: Disk usage on prod-db-01 reached 94%. Threshold is 90%.
Title: High disk usage
Source: monitoring/prometheus
Environment: Production
Timestamp: 2026-05-24 14:30:00 UTC

# Example Output
❌ **Error — High disk usage**

**What happened:** Disk usage on `prod-db-01` reached **94%**, exceeding the configured **90%** threshold.
**Impact:** The database server may run out of available storage and become unstable.
**Source:** monitoring/prometheus
**Environment:** Production
**Time (UTC):** 2026-05-24 14:30:00 UTC
**Action:** Check disk growth on `prod-db-01` and free or expand storage capacity before the volume becomes full.

Return ONLY the final Discord message.
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
