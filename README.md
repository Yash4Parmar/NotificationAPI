# NotificationAPI

A small ASP.NET Core 8 web API that accepts system notifications over HTTP. For **Warning** severity and above, it uses Google Gemini to format an on-call alert and posts it to a Discord channel via webhook.

## Table of contents

- [Features](#features)
- [Prerequisites](#prerequisites)
- [Project structure](#project-structure)
- [Getting started](#getting-started)
- [Configuration](#configuration)
- [Running the API](#running-the-api)
- [API reference](#api-reference)
- [Testing](#testing)
- [Development tools](#development-tools)
- [Behavior notes](#behavior-notes)
- [Troubleshooting](#troubleshooting)

## Features

- REST endpoint to ingest notifications with severity levels (`Trace` through `Critical`)
- LLM-powered Discord message formatting for **Warning**, **Error**, and **Critical** alerts
- Discord webhook delivery with graceful degradation when external services fail
- Fixed-window rate limiting (default: 10 requests per minute)
- Swagger UI in Development
- Unit and integration test suite with stubbed HTTP and a fake LLM

## Prerequisites

| Requirement | Version |
|-------------|---------|
| [.NET SDK](https://dotnet.microsoft.com/download) | 8.0 or later |
| Google Gemini API key | [Google AI Studio](https://aistudio.google.com/apikey) |
| Discord webhook URL (optional) | [Discord server settings → Integrations → Webhooks](https://support.discord.com/hc/en-us/articles/228383668-Intro-to-Webhooks) |

You also need internet access when running the API against real Gemini and Discord endpoints.

## Project structure

```
NotificationAPI/
├── Controllers/              # HTTP endpoints
├── Configuration/            # Options classes (Gemini, Discord, RateLimit)
├── Interfaces/               # Abstractions for LLM and Discord
├── Models/                   # Request/response DTOs and enums
├── Services/                 # Gemini client and Discord webhook sender
├── Properties/               # Launch profiles and URLs
├── NotificationAPI.Tests/    # xUnit test project
│   ├── Unit/                 # Controller and service unit tests
│   ├── Integration/          # Full HTTP pipeline tests
│   └── Helpers/              # Fakes and HTTP stubs
├── appsettings.json          # Default configuration (no secrets)
├── NotificationAPI.http      # Sample HTTP requests
└── NotificationAPI.sln       # Solution (API + Tests)
```

## Getting started

### 1. Clone and restore

```powershell
git clone <repository-url>
cd NotificationAPI
dotnet restore NotificationAPI.sln
```

This restores packages for **both** projects in the solution:

| Project | Purpose |
|---------|---------|
| `NotificationAPI` | The web API |
| `NotificationAPI.Tests` | Unit and integration tests |

### 2. Build the solution

```powershell
dotnet build NotificationAPI.sln
```

Build from the repository root to compile the API and its test project together.

### 3. Configure local settings

`appsettings.Development.json` is **gitignored** so secrets stay off the repo. Create it in the project root:

```powershell
Copy-Item appsettings.json appsettings.Development.json
```

Then edit `appsettings.Development.json` and fill in your values (see [Configuration](#configuration)).

> **Note:** At startup, the app loads `appsettings.json` only (not the standard `appsettings.{Environment}.json` layering). For local development, put your API keys and webhook URL directly in `appsettings.json`, or copy values into `appsettings.json` from your local `appsettings.Development.json` file.

## Configuration

All settings live under the root `appsettings.json` file.

### Example

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "Gemini": {
    "ApiKey": "your-gemini-api-key",
    "Model": "gemini-2.5-flash",
    "MaxTokens": 1024,
    "Temperature": 0.3,
    "MaxRetryAttempts": 3,
    "RetryBaseDelaySeconds": 2
  },
  "Discord": {
    "WebhookUrl": "https://discord.com/api/webhooks/..."
  },
  "RateLimit": {
    "PermitLimit": 10,
    "WindowMinutes": 1
  }
}
```

### Settings reference

| Section | Key | Required | Default | Description |
|---------|-----|----------|---------|-------------|
| **Gemini** | `ApiKey` | Yes | — | Google AI Studio API key. App fails at startup if missing. |
| | `Model` | No | `gemini-2.5-flash` | Gemini model name |
| | `MaxTokens` | No | `1024` | Maximum output tokens for generated alerts |
| | `Temperature` | No | `0.3` | Sampling temperature (lower = more deterministic) |
| | `MaxRetryAttempts` | No | `3` | Retries on rate limit / server errors |
| | `RetryBaseDelaySeconds` | No | `2` | Base delay for exponential backoff |
| **Discord** | `WebhookUrl` | No | — | If empty, alerts are generated but not sent to Discord |
| **RateLimit** | `PermitLimit` | No | `10` | Max requests per window |
| | `WindowMinutes` | No | `1` | Rate limit window length |

Never commit real API keys or webhook URLs. Keep secrets in local config files that are excluded from version control.

## Running the API

From the repository root:

```powershell
dotnet run --project NotificationAPI.csproj
```

Or open `NotificationAPI.sln` in Visual Studio and run the **https** or **http** profile.

| Profile | URL |
|---------|-----|
| HTTP | http://localhost:5276 |
| HTTPS | https://localhost:7166 |

In Development, Swagger opens automatically at `/swagger`.

## API reference

### `POST /api/notifications`

Accepts a notification and returns **202 Accepted** immediately. Alert generation and Discord delivery run as part of the request; failures in Gemini or Discord are logged but do not change the HTTP status.

#### Request body

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `level` | string enum | Yes | `Trace`, `Debug`, `Info`, `Warning`, `Error`, or `Critical` |
| `message` | string | Yes | Notification text (max 4000 characters) |
| `title` | string | No | Short title (max 200 characters) |
| `source` | string | No | Origin system or service (max 200 characters) |
| `timestamp` | ISO 8601 datetime | No | Event time; defaults to UTC now |

#### Response body (`202 Accepted`)

| Field | Type | Description |
|-------|------|-------------|
| `id` | GUID | Unique ID assigned to this notification |
| `receivedAt` | datetime | UTC timestamp when the API received the request |
| `level` | string enum | Echo of the submitted level |
| `generatedMessage` | string \| null | Discord-ready text from Gemini; present only for **Warning+** when generation succeeds |

#### Severity behavior

| Level | Gemini | Discord |
|-------|--------|---------|
| `Trace`, `Debug`, `Info` | Skipped | Skipped |
| `Warning`, `Error`, `Critical` | Message generated | Sent if webhook URL is configured |

#### Rate limiting

When the limit is exceeded, the API returns **429 Too Many Requests** with a `Retry-After` header (seconds).

Default: **10 requests per minute** per policy.

#### Examples

**Info** (accepted, no alert):

```powershell
curl -X POST http://localhost:5276/api/notifications `
  -H "Content-Type: application/json" `
  -d '{"level":"Info","message":"Scheduled backup completed successfully"}'
```

**Warning** (triggers Gemini + Discord):

```powershell
curl -X POST http://localhost:5276/api/notifications `
  -H "Content-Type: application/json" `
  -d '{
    "level": "Warning",
    "message": "Disk usage on srv-01 exceeded 90%",
    "title": "Storage alert",
    "source": "monitoring-agent",
    "timestamp": "2026-05-22T14:30:00Z"
  }'
```

Sample response:

```json
{
  "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "receivedAt": "2026-05-24T10:15:00Z",
  "level": "Warning",
  "generatedMessage": "⚠️ **Warning — Storage**\n\n**What happened:** ..."
}
```

## Testing

The `NotificationAPI.Tests` project uses **xUnit**, **FluentAssertions**, **Moq**, and **Microsoft.AspNetCore.Mvc.Testing**.

Tests do **not** call real Gemini or Discord APIs. HTTP clients and the LLM are stubbed or faked.

### Run all tests

From the repository root:

```powershell
dotnet test NotificationAPI.sln
```

### Run with verbose output

```powershell
dotnet test NotificationAPI.sln --verbosity normal
```

### Run a specific test project

```powershell
dotnet test NotificationAPI.Tests/NotificationAPI.Tests.csproj
```

### What is covered

| Area | Location | Coverage |
|------|----------|----------|
| Controller logic | `Unit/Controllers/` | Level filtering (Info vs Warning+), error resilience when Gemini or Discord fails |
| Discord sender | `Unit/Services/` | Webhook POST behavior, missing URL handling |
| HTTP pipeline | `Integration/` | Validation, JSON enum deserialization, Warning+ flow with fake LLM |
| Rate limiting | `Integration/RateLimitTests.cs` | 429 responses and `Retry-After` header |

### Run tests from Visual Studio

Open `NotificationAPI.sln`, build the solution, then use **Test Explorer** to run or debug individual tests.

## Development tools

### Swagger

Available in **Development** only:

- http://localhost:5276/swagger

Use it to inspect schemas and send trial requests.

### HTTP file

`NotificationAPI.http` contains ready-made requests for VS Code REST Client, Visual Studio, or Rider. Update `@NotificationAPI_HostAddress` if your local port differs from `http://localhost:5276`.

## Behavior notes

- The API always returns **202 Accepted** for valid requests, even when Gemini or Discord fails afterward.
- If the Discord webhook URL is not set, generated messages are still returned in the response but nothing is posted to Discord.
- Gemini retries automatically on rate limits (429), service unavailable (503), and gateway timeout (504).
- Enum values in JSON are **case-sensitive strings** (e.g. `"Warning"`, not `"warning"`).

## Troubleshooting

| Problem | Likely cause | Fix |
|---------|--------------|-----|
| Startup exception about Gemini API key | `Gemini:ApiKey` is empty | Set the key in `appsettings.json` |
| 429 from this API | Rate limit exceeded | Wait for the window to reset; check `Retry-After` header |
| No Discord message | Webhook URL missing or invalid | Set `Discord:WebhookUrl`; check application logs |
| Gemini errors in logs | Quota, network, or model name | Verify API key, model name, and connectivity |
| Tests fail to build | SDK or package mismatch | Run `dotnet restore` and ensure .NET 8 SDK is installed |
| Wrong local URL | Port changed in launch profile | Check console output or `Properties/launchSettings.json` |
