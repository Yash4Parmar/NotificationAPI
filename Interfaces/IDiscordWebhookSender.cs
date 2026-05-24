namespace NotificationAPI.Interfaces;

public interface IDiscordWebhookSender
{
    Task SendMessageAsync(string content, CancellationToken cancellationToken = default);
}
