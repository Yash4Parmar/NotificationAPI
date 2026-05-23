using System.Net;
using Microsoft.AspNetCore.Mvc;
using NotificationAPI.Interfaces;
using NotificationAPI.Models;
using NotificationAPI.Services;

namespace NotificationAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class NotificationsController : ControllerBase
{
    private readonly ILlmMessageGenerator _llmMessageGenerator;
    private readonly ILogger<NotificationsController> _logger;

    public NotificationsController(
        ILlmMessageGenerator llmMessageGenerator,
        ILogger<NotificationsController> logger)
    {
        _llmMessageGenerator = llmMessageGenerator;
        _logger = logger;
    }

    [HttpPost]
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
