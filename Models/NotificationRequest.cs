using System.ComponentModel.DataAnnotations;

namespace NotificationAPI.Models;

public class NotificationRequest
{
    [Required]
    public NotificationLevel Level { get; set; }

    [Required]
    [MaxLength(4000)]
    public string Message { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? Title { get; set; }

    [MaxLength(200)]
    public string? Source { get; set; }

    public DateTime? Timestamp { get; set; }

}
