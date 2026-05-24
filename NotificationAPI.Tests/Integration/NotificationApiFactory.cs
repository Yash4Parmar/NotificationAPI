using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NotificationAPI.Configuration;
using NotificationAPI.Interfaces;
using NotificationAPI.Services;
using NotificationAPI.Tests.Helpers;

namespace NotificationAPI.Tests.Integration;

public sealed class NotificationApiFactory : WebApplicationFactory<Program>
{
    public StubHttpMessageHandler DiscordHandler { get; } =
        new(HttpStatusCode.NoContent);

    private readonly Dictionary<string, string?> _configOverrides = new()
    {
        ["Gemini:ApiKey"] = "test-key",
        ["Discord:WebhookUrl"] = "https://discord.test/webhook",
        ["RateLimit:PermitLimit"] = "10",
        ["RateLimit:WindowMinutes"] = "1"
    };

    public NotificationApiFactory WithConfig(string key, string value)
    {
        _configOverrides[key] = value;
        return this;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.Sources.Clear();
            config.AddInMemoryCollection(_configOverrides);
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ILlmMessageGenerator>();
            services.AddSingleton<ILlmMessageGenerator, FakeLlmMessageGenerator>();

            services.RemoveAll<IDiscordWebhookSender>();
            services.AddSingleton<IDiscordWebhookSender>(sp =>
            {
                var options = sp.GetRequiredService<IOptions<DiscordOptions>>();
                var logger = sp.GetRequiredService<ILogger<DiscordWebhookSender>>();
                var httpClient = new HttpClient(DiscordHandler);
                return new DiscordWebhookSender(httpClient, options, logger);
            });
        });
    }
}
