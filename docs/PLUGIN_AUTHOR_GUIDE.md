# HirschNotify Plugin Author Guide

This guide shows you how to build a contact method plugin for HirschNotify — a DLL
you can drop into the `plugins/` directory to add a new notification channel (SMS,
Slack, webhook, PagerDuty, etc.) without modifying or recompiling the main app.

## How plugins work

At startup HirschNotify scans the `plugins/` directory next to the binary, loads
every `.dll` it finds, and registers any class implementing `IContactMethodSender`
in the dependency injection container. Those senders are discovered automatically
by:

- The **Integrations page** — renders a card with your plugin's icon, name,
  description, and a dynamically-generated configuration form
- The **NotificationSender** — dispatches notifications through your plugin
  whenever a matching recipient contact method fires
- The **Recipients edit page** — shows your plugin in the contact method type
  dropdown

**You do not need to touch the main app.** Build your DLL, drop it in `plugins/`,
restart the service, and it appears.

## Prerequisites

- .NET 10 SDK (match the host app's target framework — check `HirschNotify.csproj`)
- A reference to `HirschNotify.dll` (the main app binary) OR a shared interface
  assembly if you're shipping to third parties
- Familiarity with dependency injection and async C#

## The interface

Every plugin implements this single interface from `HirschNotify.Services`:

```csharp
public interface IContactMethodSender
{
    // ── Identity ──
    string Type { get; }          // short id, e.g. "sms", "slack", "webhook"
                                  // must be unique across all plugins
    string DisplayName { get; }   // e.g. "SMS (Twilio)"
    string Description { get; }   // shown under the name on the integration card
    string IconSvg { get; }       // inline SVG markup for the card icon

    // ── Admin UI form definition ──
    ContactMethodField[] ConfigurationFields { get; }

    // ── Runtime behavior ──
    Task<bool> SendAsync(ContactMethod method, string subject, string body);
    string? ValidateConfiguration(string configurationJson);
}
```

And the field descriptor:

```csharp
public record ContactMethodField(
    string Key,            // settings key suffix (e.g. "AccountSid")
    string Label,          // form label shown to the user
    string Type,           // "text" | "email" | "number" | "password" | "select"
    string? Placeholder = null,
    string? HelpText = null,
    bool IsSecret = false  // encrypted at rest when true
);
```

## Two kinds of configuration

This is the most important concept to grasp before writing a plugin.

### 1. Global plugin settings

Stored in the `AppSettings` table, keyed by `ContactMethod:{type}:{key}`. These are
set once by the admin on the Integrations page and apply to **every** use of the
plugin. Examples:

- SMTP host, port, credentials
- Twilio account SID and auth token
- Slack workspace API token

You declare these via `ConfigurationFields` and read them inside `SendAsync` using
the injected `ISettingsService`.

### 2. Per-recipient configuration

Stored in the `ContactMethod.Configuration` column as a JSON blob. Each recipient's
instance of your contact method has its own values. Examples:

- A recipient's email address
- A recipient's phone number
- A recipient's Slack channel or user ID

You define the JSON shape yourself. `ValidateConfiguration` is called when a user
adds a contact method of your type, so you can reject invalid values before they
hit the database.

## Writing a plugin: complete example

Here is a complete Twilio SMS plugin. Create a new class library project:

```bash
dotnet new classlib -n HirschNotify.Sms.Twilio -f net10.0
cd HirschNotify.Sms.Twilio
dotnet add reference ../HirschNotify/HirschNotify.csproj
dotnet add package Twilio
```

Then create `TwilioSmsSender.cs`:

```csharp
using System.Text.Json;
using HirschNotify.Models;
using HirschNotify.Services;
using Microsoft.Extensions.Logging;
using Twilio;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Types;

namespace HirschNotify.Sms.Twilio;

public class TwilioSmsSender : IContactMethodSender
{
    public string Type => "sms-twilio";
    public string DisplayName => "SMS (Twilio)";
    public string Description => "Send SMS notifications via Twilio.";

    public string IconSvg => """
        <svg width="24" height="24" viewBox="0 0 24 24" fill="none"
             stroke="currentColor" stroke-width="2" stroke-linecap="round"
             stroke-linejoin="round">
          <path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"/>
        </svg>
        """;

    public ContactMethodField[] ConfigurationFields =>
    [
        new("AccountSid", "Account SID", "text", "ACxxxxxxxx..."),
        new("AuthToken", "Auth Token", "password", null,
            "Your Twilio auth token.", IsSecret: true),
        new("FromNumber", "From Number", "text", "+15551234567",
            "The Twilio phone number to send from (E.164 format)."),
    ];

    private readonly ISettingsService _settings;
    private readonly ILogger<TwilioSmsSender> _logger;

    public TwilioSmsSender(ISettingsService settings, ILogger<TwilioSmsSender> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public async Task<bool> SendAsync(ContactMethod method, string subject, string body)
    {
        try
        {
            var config = JsonSerializer.Deserialize<SmsConfig>(method.Configuration);
            if (config == null || string.IsNullOrWhiteSpace(config.PhoneNumber))
            {
                _logger.LogWarning("Invalid SMS config for ContactMethod {Id}", method.Id);
                return false;
            }

            var accountSid = await _settings.GetAsync("ContactMethod:sms-twilio:AccountSid");
            var authToken  = await _settings.GetEncryptedAsync("ContactMethod:sms-twilio:AuthToken");
            var fromNumber = await _settings.GetAsync("ContactMethod:sms-twilio:FromNumber");

            if (string.IsNullOrEmpty(accountSid) ||
                string.IsNullOrEmpty(authToken) ||
                string.IsNullOrEmpty(fromNumber))
            {
                _logger.LogWarning("Twilio not fully configured — skipping SMS");
                return false;
            }

            TwilioClient.Init(accountSid, authToken);

            await MessageResource.CreateAsync(
                to: new PhoneNumber(config.PhoneNumber),
                from: new PhoneNumber(fromNumber),
                body: $"{subject}: {body}"
            );

            _logger.LogInformation("SMS sent to {Number} for ContactMethod {Id}",
                config.PhoneNumber, method.Id);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send SMS for ContactMethod {Id}", method.Id);
            return false;
        }
    }

    public string? ValidateConfiguration(string configurationJson)
    {
        try
        {
            var config = JsonSerializer.Deserialize<SmsConfig>(configurationJson);
            if (config == null || string.IsNullOrWhiteSpace(config.PhoneNumber))
                return "Phone number is required.";
            if (!config.PhoneNumber.StartsWith('+'))
                return "Phone number must be in E.164 format (e.g. +15551234567).";
            return null;
        }
        catch
        {
            return "Invalid configuration format.";
        }
    }

    private class SmsConfig
    {
        public string PhoneNumber { get; set; } = string.Empty;
    }
}
```

That's the whole plugin. Build it and deploy:

```bash
dotnet build -c Release
cp bin/Release/net10.0/HirschNotify.Sms.Twilio.dll \
   /path/to/HirschNotify/plugins/
# Copy any third-party dependencies your plugin pulled in too:
cp bin/Release/net10.0/Twilio.dll /path/to/HirschNotify/plugins/
```

Restart the HirschNotify service. The Integrations page now shows an "SMS (Twilio)"
card. Fill in your Twilio credentials, enable it, and edit a recipient to add a
phone number as a contact method.

## Interface reference details

### `Type`

A short, unique, URL-safe identifier. Used as:
- The key in `ContactMethod.Type` column
- The middle segment in settings keys: `ContactMethod:{type}:...`
- The option value in the recipient edit form's type dropdown

Use lowercase, no spaces. Prefix with your organization or project if you expect
conflicts with built-in types: `acme-pagerduty`, not `pagerduty`.

### `IconSvg`

Inline SVG rendered directly in the Integrations card. Use `currentColor` for stroke
or fill so it inherits the card's theme color. Keep the viewBox around 24x24. You
can copy icons from [Lucide](https://lucide.dev) or any icon set — just paste the
`<svg>` markup as a C# raw string.

### `ConfigurationFields`

Describes the form the Integrations page renders for global plugin settings.

**Field types:**

| `Type` value | Rendered as                         |
| ------------ | ----------------------------------- |
| `text`       | `<input type="text">`               |
| `email`      | `<input type="email">`              |
| `number`     | `<input type="number">`             |
| `password`   | `<input type="password">`           |
| `select`     | Yes/No dropdown (booleans)          |

**Set `IsSecret: true`** for any field containing credentials, API keys, or tokens.
The value is stored encrypted via `ISettingsService.SetEncryptedAsync` and the form
displays placeholder dots instead of the actual value on reload.

### `SendAsync(ContactMethod method, string subject, string body)`

Called once per recipient whose filter rule matched. You receive:

- `method.Configuration` — the per-recipient JSON blob you defined
- `subject` — always `"Alert"` today
- `body` — the rendered message text (templated by the rule)

Return `true` if the message was accepted by the remote service (the SMTP server
acknowledged the email, Twilio returned a message SID, Slack returned 200 OK).
Return `false` on any failure — log the details with the injected `ILogger`, don't
throw exceptions from this method.

### `ValidateConfiguration(string configurationJson)`

Called when a user adds or edits a contact method of your type on the recipient
edit page. Return `null` if valid, or an error message string if not. The UI shows
your error message to the user and blocks the save.

This is your chance to catch bad phone numbers, malformed Slack channel IDs,
invalid webhook URLs, etc. before they reach `SendAsync`.

## Settings access

All global plugin settings are read via the injected `ISettingsService`:

```csharp
await _settings.GetAsync("ContactMethod:sms-twilio:AccountSid")           // plaintext
await _settings.GetEncryptedAsync("ContactMethod:sms-twilio:AuthToken")   // decrypted
```

The key format is **always** `ContactMethod:{your-type}:{FieldKey}`. The Integrations
page handles the write side automatically — when the admin saves the config form,
plain fields go through `SetAsync` and fields marked `IsSecret` go through
`SetEncryptedAsync`.

Never read or write anything outside your `ContactMethod:{type}:*` namespace. Other
namespaces belong to the core app and other plugins.

## Logging

Inject `ILogger<YourSender>` and use it for everything. Log at `Information` level
for successful sends (include the target identifier — phone number, email, channel
name), `Warning` for configuration problems or skipped sends, and `Error` for
actual failures. All logs flow through Serilog into the app's rolling file log.

## Dependency injection

Your sender class is registered as **scoped**, matching the built-in EmailSender.
You can inject:

- `ISettingsService` — for reading plugin settings
- `ILogger<T>` — for structured logging
- `HttpClient` — if you need one, inject it directly (the host registers a default)
- Any other service registered in the host's DI container

You **cannot** register your own services from a plugin — the plugin loader only
looks for `IContactMethodSender` implementations and registers those. If your plugin
needs helper services, make them internal classes constructed by your sender.

## Deployment

1. Build your plugin in Release mode
2. Copy the plugin DLL **and any third-party dependencies it pulled in** into the
   host's `plugins/` directory
3. Restart the HirschNotify service

The plugins directory is created empty on first run if it doesn't exist. Its
location is `{AppContext.BaseDirectory}/plugins` — alongside `HirschNotify.dll`.

**Don't** ship `HirschNotify.dll` itself in your plugin output. It's already loaded
by the host. If your plugin references the host project, use `<Private>false</Private>`
on the ProjectReference or mark it as a framework reference so the build doesn't
copy it.

### NuGet dependency conflicts

Plugins load into the default `AssemblyLoadContext`, so they share dependency
versions with the host. If your plugin needs a different version of a package the
host already uses, you'll hit a loader conflict at runtime.

**Best practice:** build your plugin against the exact same SDK version and package
versions the host uses. If you need true isolation for conflicting dependencies,
you'd need to extend the host to use a dedicated `AssemblyLoadContext` per plugin —
file an issue if that becomes necessary.

## Testing locally

The fastest feedback loop:

1. Open the host app in your IDE of choice
2. Reference your plugin project directly in `HirschNotify.csproj` during development
3. Debug — set breakpoints in `SendAsync`
4. Once working, remove the ProjectReference and do a release build + drop into
   `plugins/` to verify the discovery path works

Alternatively, use `dotnet watch` on the host and symlink your plugin's output DLL
into the `plugins/` directory — the host restarts on file changes.

## Caveats and gotchas

- **Type uniqueness.** If two plugins register the same `Type` string, the second
  one wins in the DI resolution and the first becomes unreachable. Namespace your
  type ids.
- **No unloading.** Plugins stay loaded until the process exits. There is no
  hot-reload — restart the service to pick up changes.
- **Exceptions in the constructor kill startup.** If your sender's constructor
  throws (e.g. validating config at construction time), the whole app fails to
  start. Defer validation to `SendAsync` or `ValidateConfiguration`.
- **Scoped lifetime.** A new instance of your sender is created per notification.
  Don't cache state on instance fields — use a static field or inject a singleton.
- **`ContactMethod.Configuration` is a string.** Always use `JsonSerializer` with a
  small internal class to parse it. Never trust its contents — it came from a user
  form and your `ValidateConfiguration` should have been your last line of defense.

## Reference

- Main interface: `internal/Services/IContactMethodSender.cs`
- Built-in example: `internal/Services/EmailSender.cs`
- Plugin loader: `Program.cs` (search for `pluginsDir`)
- Settings service: `internal/Services/SettingsService.cs`
- ContactMethod model: `internal/Models/ContactMethod.cs`
