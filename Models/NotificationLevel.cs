using System.Text.Json.Serialization;

namespace NotificationAPI.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NotificationLevel
{
    Trace,
    Debug,
    Info,
    Warning,
    Error,
    Critical
}
