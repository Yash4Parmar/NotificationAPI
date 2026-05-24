using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NotificationAPI.Controllers;
using NotificationAPI.Interfaces;
using NotificationAPI.Models;
using NotificationAPI.Services;

namespace NotificationAPI.Tests.Unit.Controllers;

public class NotificationsControllerTests
{
    private readonly Mock<ILlmMessageGenerator> _llmMock = new();
    private readonly Mock<IDiscordWebhookSender> _discordMock = new();
    private readonly NotificationsController _controller;

    public NotificationsControllerTests()
    {
        _controller = new NotificationsController(
            _llmMock.Object,
            _discordMock.Object,
            NullLogger<NotificationsController>.Instance);
    }

    [Fact]
    public async Task Post_Info_DoesNotCallLlmOrDiscord()
    {
        var request = new NotificationRequest
        {
            Level = NotificationLevel.Info,
            Message = "Backup completed"
        };

        var result = await _controller.Post(request, CancellationToken.None);

        var accepted = result.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        accepted.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        var body = accepted.Value.Should().BeOfType<NotificationAcceptedResponse>().Subject;
        body.Level.Should().Be(NotificationLevel.Info);
        body.GeneratedMessage.Should().BeNull();

        _llmMock.Verify(
            m => m.GenerateMessageAsync(It.IsAny<NotificationRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _discordMock.Verify(
            m => m.SendMessageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData(NotificationLevel.Warning)]
    [InlineData(NotificationLevel.Error)]
    [InlineData(NotificationLevel.Critical)]
    public async Task Post_WarningOrAbove_CallsLlmAndDiscord(NotificationLevel level)
    {
        const string generated = "Alert message";
        var request = new NotificationRequest
        {
            Level = level,
            Message = "Disk usage high"
        };

        _llmMock
            .Setup(m => m.GenerateMessageAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(generated);

        var result = await _controller.Post(request, CancellationToken.None);

        var body = result.Result.Should().BeAssignableTo<ObjectResult>().Subject.Value
            .Should().BeOfType<NotificationAcceptedResponse>().Subject;
        body.GeneratedMessage.Should().Be(generated);

        _llmMock.Verify(
            m => m.GenerateMessageAsync(request, It.IsAny<CancellationToken>()),
            Times.Once);
        _discordMock.Verify(
            m => m.SendMessageAsync(generated, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Post_GeminiRateLimit_StillAccepted()
    {
        var request = new NotificationRequest
        {
            Level = NotificationLevel.Warning,
            Message = "Something went wrong"
        };

        _llmMock
            .Setup(m => m.GenerateMessageAsync(request, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GeminiApiException(HttpStatusCode.TooManyRequests, "quota", "Rate limited"));

        var result = await _controller.Post(request, CancellationToken.None);

        result.Result.Should().BeAssignableTo<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        _discordMock.Verify(
            m => m.SendMessageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Post_GeminiOtherError_StillAccepted()
    {
        var request = new NotificationRequest
        {
            Level = NotificationLevel.Warning,
            Message = "Something went wrong"
        };

        _llmMock
            .Setup(m => m.GenerateMessageAsync(request, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GeminiApiException(HttpStatusCode.InternalServerError, "error", "Server error"));

        var result = await _controller.Post(request, CancellationToken.None);

        result.Result.Should().BeAssignableTo<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        _discordMock.Verify(
            m => m.SendMessageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Post_GeminiNetworkError_StillAccepted()
    {
        var request = new NotificationRequest
        {
            Level = NotificationLevel.Warning,
            Message = "Something went wrong"
        };

        _llmMock
            .Setup(m => m.GenerateMessageAsync(request, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Network error"));

        var result = await _controller.Post(request, CancellationToken.None);

        result.Result.Should().BeAssignableTo<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status202Accepted);
    }

    [Fact]
    public async Task Post_DiscordFailure_StillAccepted()
    {
        const string generated = "Alert message";
        var request = new NotificationRequest
        {
            Level = NotificationLevel.Warning,
            Message = "Disk usage high"
        };

        _llmMock
            .Setup(m => m.GenerateMessageAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(generated);
        _discordMock
            .Setup(m => m.SendMessageAsync(generated, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Discord unavailable"));

        var result = await _controller.Post(request, CancellationToken.None);

        var body = result.Result.Should().BeAssignableTo<ObjectResult>().Subject.Value
            .Should().BeOfType<NotificationAcceptedResponse>().Subject;
        body.GeneratedMessage.Should().Be(generated);
    }
}
