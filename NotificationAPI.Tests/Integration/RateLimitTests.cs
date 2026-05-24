using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using NotificationAPI.Models;

namespace NotificationAPI.Tests.Integration;

public class RateLimitTests : IDisposable
{
    private readonly NotificationApiFactory _factory;
    private readonly HttpClient _client;

    public RateLimitTests()
    {
        _factory = new NotificationApiFactory();
        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task Post_ExceedsRateLimit_Returns429()
    {
        var request = new NotificationRequest
        {
            Level = NotificationLevel.Info,
            Message = "Rate limit probe"
        };

        for (var i = 0; i < 10; i++)
        {
            var response = await _client.PostAsJsonAsync("/api/notifications", request);
            response.StatusCode.Should().Be(HttpStatusCode.Accepted, $"request {i + 1} should succeed");
        }

        var exceeded = await _client.PostAsJsonAsync("/api/notifications", request);
        exceeded.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        var body = await exceeded.Content.ReadAsStringAsync();
        body.Should().Contain("Rate limit exceeded");
    }

    public void Dispose() => _factory.Dispose();
}
