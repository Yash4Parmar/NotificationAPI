using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NotificationAPI.Configuration;
using NotificationAPI.Services;
using NotificationAPI.Tests.Helpers;

namespace NotificationAPI.Tests.Unit.Services;

public class DiscordWebhookSenderTests
{
    [Fact]
    public async Task SendMessageAsync_EmptyWebhook_SkipsHttpCall()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.NoContent);
        var sender = CreateSender(handler, webhookUrl: "");

        await sender.SendMessageAsync("hello");

        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task SendMessageAsync_Success_DoesNotThrow()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.NoContent);
        var sender = CreateSender(handler);

        var act = () => sender.SendMessageAsync("hello");

        await act.Should().NotThrowAsync();
        handler.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task SendMessageAsync_Failure_ThrowsHttpRequestException()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.InternalServerError);
        var sender = CreateSender(handler);

        var act = () => sender.SendMessageAsync("hello");

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task SendMessageAsync_SendsCorrectJsonBody()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.NoContent);
        var sender = CreateSender(handler);
        const string content = "Test Discord message";

        await sender.SendMessageAsync(content);

        var request = handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri!.ToString().Should().Be("https://discord.test/webhook");

        var body = await request.Content!.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("content").GetString().Should().Be(content);
    }

    private static DiscordWebhookSender CreateSender(
        StubHttpMessageHandler handler,
        string webhookUrl = "https://discord.test/webhook")
    {
        var options = Options.Create(new DiscordOptions { WebhookUrl = webhookUrl });
        var httpClient = new HttpClient(handler);
        return new DiscordWebhookSender(httpClient, options, NullLogger<DiscordWebhookSender>.Instance);
    }
}
