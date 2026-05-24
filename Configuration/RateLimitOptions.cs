namespace NotificationAPI.Configuration;

public class RateLimitOptions
{
    public const string SectionName = "RateLimit";

    public int PermitLimit { get; set; } = 10;
    public int WindowMinutes { get; set; } = 1;
}
