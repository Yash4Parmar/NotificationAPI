using NotificationAPI.Interfaces;
using NotificationAPI.Models;

namespace NotificationAPI.Tests.Helpers;

public sealed class FakeLlmMessageGenerator : ILlmMessageGenerator
{
    public const string DefaultMessage = "Test alert message";

    public Task<string> GenerateMessageAsync(
        NotificationRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(DefaultMessage);
}
