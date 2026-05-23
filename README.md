# NotificationAPI

Receives notifications over HTTP. For Warning and above, calls Google Gemini to generate an alert message.

## Configuration

Set `Gemini` in `appsettings.json` or `appsettings.Development.json`:

- `ApiKey` – from [Google AI Studio](https://aistudio.google.com/apikey)
- `Model` – e.g. `gemini-2.0-flash`

## Run

```powershell
dotnet run
```

POST `/api/notifications` with a JSON body (see `NotificationAPI.http`).
