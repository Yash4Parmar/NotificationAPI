namespace NotificationAPI.Configuration;

public class DiscordOptions
{
    public const string SectionName = "Discord";

    public string WebhookUrl { get; set; } = string.Empty;
}
