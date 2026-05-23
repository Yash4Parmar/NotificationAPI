using NotificationAPI.Models;

namespace NotificationAPI.Interfaces;

public interface ILlmMessageGenerator
{
    Task<string> GenerateMessageAsync(NotificationRequest request, CancellationToken cancellationToken = default);
}
