using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using NotificationAPI.Models;
using NotificationAPI.Tests.Helpers;

namespace NotificationAPI.Tests.Integration;

public class NotificationsEndpointTests : IClassFixture<NotificationApiFactory>
{
    private readonly HttpClient _client;
    private readonly NotificationApiFactory _factory;

    public NotificationsEndpointTests(NotificationApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Post_Info_Returns202WithoutGeneratedMessage()
    {
        var request = new NotificationRequest
        {
            Level = NotificationLevel.Info,
            Message = "Scheduled backup completed"
        };

        var response = await _client.PostAsJsonAsync("/api/notifications", request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = await response.Content.ReadFromJsonAsync<NotificationAcceptedResponse>();
        body.Should().NotBeNull();
        body!.Id.Should().NotBeEmpty();
        body.ReceivedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        body.Level.Should().Be(NotificationLevel.Info);
        body.GeneratedMessage.Should().BeNull();
    }

    [Fact]
    public async Task Post_Warning_Returns202WithGeneratedMessage()
    {
        _factory.DiscordHandler.Requests.Clear();

        var request = new NotificationRequest
        {
            Level = NotificationLevel.Warning,
            Message = "Disk usage exceeded 90%"
        };

        var response = await _client.PostAsJsonAsync("/api/notifications", request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = await response.Content.ReadFromJsonAsync<NotificationAcceptedResponse>();
        body!.GeneratedMessage.Should().Be(FakeLlmMessageGenerator.DefaultMessage);
        _factory.DiscordHandler.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task Post_MissingMessage_Returns400()
    {
        var json = """{"level":"Info"}""";
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _client.PostAsync("/api/notifications", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Post_InvalidLevel_Returns400()
    {
        var json = """{"level":"Banana","message":"test"}""";
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _client.PostAsync("/api/notifications", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Post_StringEnumLevel_DeserializesCorrectly()
    {
        var json = """{"level":"Warning","message":"CPU spike detected"}""";
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _client.PostAsync("/api/notifications", content);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = await response.Content.ReadFromJsonAsync<NotificationAcceptedResponse>();
        body!.Level.Should().Be(NotificationLevel.Warning);
    }
}
