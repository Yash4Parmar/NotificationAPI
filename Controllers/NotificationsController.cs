using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NotificationAPI.Interfaces;
using NotificationAPI.Models;
using NotificationAPI.Services;

namespace NotificationAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class NotificationsController : ControllerBase
{
    private readonly ILlmMessageGenerator _llmMessageGenerator;
    private readonly DiscordWebhookSender _discordWebhookSender;
    private readonly ILogger<NotificationsController> _logger;

    public NotificationsController(
        ILlmMessageGenerator llmMessageGenerator,
        DiscordWebhookSender discordWebhookSender,
        ILogger<NotificationsController> logger)
    {
        _llmMessageGenerator = llmMessageGenerator;
        _discordWebhookSender = discordWebhookSender;
        _logger = logger;
    }

    [HttpPost]
    [EnableRateLimiting("notifications")]
    public async Task<ActionResult<NotificationAcceptedResponse>> Post(
        [FromBody] NotificationRequest request,
        CancellationToken cancellationToken)
    {
        var response = new NotificationAcceptedResponse
        {
            Id = Guid.NewGuid(),
            ReceivedAt = DateTime.UtcNow,
            Level = request.Level
        };

        if (request.Level >= NotificationLevel.Warning)
        {
            try
            {
                var alertMessage = await _llmMessageGenerator.GenerateMessageAsync(request, cancellationToken);
                response.GeneratedMessage = alertMessage;
                _logger.LogInformation("Generated Discord alert for {Id}: {AlertMessage}", response.Id, alertMessage);

                try
                {
                    await _discordWebhookSender.SendMessageAsync(alertMessage, cancellationToken);
                    _logger.LogInformation("Discord webhook sent for {Id}", response.Id);
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogError(
                        ex,
                        "Notification {Id} accepted but Discord webhook failed.",
                        response.Id);
                }
            }
            catch (GeminiApiException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _logger.LogWarning(
                    ex,
                    "Notification {Id} accepted but Gemini rate limit blocked alert generation. Response: {GeminiBody}",
                    response.Id,
                    ex.ResponseBody);
            }
            catch (GeminiApiException ex)
            {
                _logger.LogError(
                    ex,
                    "Notification {Id} accepted but Gemini failed with {StatusCode}. Response: {GeminiBody}",
                    response.Id,
                    (int)ex.StatusCode,
                    ex.ResponseBody);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(
                    ex,
                    "Notification {Id} accepted but could not reach Gemini (DNS or network). Check internet, DNS, VPN, and firewall.",
                    response.Id);
            }
        }

        return Accepted(response);
    }
}
