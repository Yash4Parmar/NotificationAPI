namespace NotificationAPI.Models;

public class NotificationAcceptedResponse
{
    public Guid Id { get; set; }

    public DateTime ReceivedAt { get; set; }

    public NotificationLevel Level { get; set; }

    /// <summary>Discord-ready message from the LLM. Present when level is Warning or above and generation succeeded.</summary>
    public string? GeneratedMessage { get; set; }
}
