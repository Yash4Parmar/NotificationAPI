using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using NotificationAPI.Configuration;
using NotificationAPI.Interfaces;
using NotificationAPI.Services;

namespace NotificationAPI
{
    public partial class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            if (!builder.Environment.IsEnvironment("Testing"))
            {
                builder.Configuration.Sources.Clear();
                builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
            }

            // Add services to the container.

            builder.Services.AddControllers()
                .AddJsonOptions(options =>
                {
                    options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
                });

            builder.Services.Configure<GeminiOptions>(builder.Configuration.GetSection(GeminiOptions.SectionName));
            builder.Services.Configure<DiscordOptions>(builder.Configuration.GetSection(DiscordOptions.SectionName));
            builder.Services.Configure<RateLimitOptions>(builder.Configuration.GetSection(RateLimitOptions.SectionName));

            var rateLimitOptions = builder.Configuration
                .GetSection(RateLimitOptions.SectionName)
                .Get<RateLimitOptions>() ?? new RateLimitOptions();

            builder.Services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

                options.AddPolicy("notifications", _ =>
                    RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey: "notifications",
                        factory: _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = rateLimitOptions.PermitLimit,
                            Window = TimeSpan.FromMinutes(rateLimitOptions.WindowMinutes),
                            QueueLimit = 0
                        }));

                options.OnRejected = async (context, token) =>
                {
                    if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    {
                        context.HttpContext.Response.Headers.RetryAfter =
                            ((int)retryAfter.TotalSeconds).ToString();
                    }

                    context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    await context.HttpContext.Response.WriteAsync(
                        "Rate limit exceeded. Maximum 10 requests per minute.",
                        token);
                };
            });

            builder.Services.AddSingleton<ILlmMessageGenerator, GeminiMessageGenerator>();
            builder.Services.AddHttpClient<IDiscordWebhookSender, DiscordWebhookSender>();

            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();

            var app = builder.Build();

            // Configure the HTTP request pipeline.
            if (app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
            }

            app.UseHttpsRedirection();

            app.UseRateLimiter();

            app.UseAuthorization();

            app.MapControllers();

            app.Run();
        }
    }
}
