using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using NotificationAPI.Configuration;
using NotificationAPI.Interfaces;

namespace NotificationAPI.Services;

public class DiscordWebhookSender : IDiscordWebhookSender
{
    private readonly HttpClient _httpClient;
    private readonly DiscordOptions _options;
    private readonly ILogger<DiscordWebhookSender> _logger;

    public DiscordWebhookSender(
        HttpClient httpClient,
        IOptions<DiscordOptions> options,
        ILogger<DiscordWebhookSender> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task SendMessageAsync(string content, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.WebhookUrl))
        {
            _logger.LogWarning("Discord webhook URL is not configured. Skipping send.");
            return;
        }

        var response = await _httpClient.PostAsJsonAsync(
            _options.WebhookUrl,
            new { content },
            cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            _logger.LogInformation("Discord webhook message sent successfully.");
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogError(
            "Discord webhook failed with {StatusCode}. Response: {Body}",
            (int)response.StatusCode,
            body);
        response.EnsureSuccessStatusCode();
    }
}
