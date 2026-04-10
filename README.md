# HirschNotify

Windows service that bridges the **Hirsch / Identiv Velocity** access control
system to the **HirschNotify mobile app**, with a Razor Pages web UI for
configuring filter rules, recipients, throttling, and SRE-facing health
monitoring.

HirschNotify ingests Velocity events (door reads, alarms, transactions,
software events, and now Velocity stack health), evaluates them against
user-defined filter rules, and dispatches notifications through the
HirschRelay → HirschNotifyMobile delivery chain.

---

## Architecture at a glance

```
                ┌─────────────────────────────────────────┐
                │              HirschNotify               │
                │           (this .NET service)           │
                │                                         │
  Velocity ─────┤  VelocityAdapterWorker  ─┐              │
  (SDK / TCP)   │                          │              │
                │  WebSocketWorker  ───────┤              │
                │                          ▼              │
                │                  IEventProcessor        │
                │                          │              │
                │                          ▼              │
                │               FilterEngine + Throttle   │
                │                          │              │
                │                          ▼              │
                │                  NotificationSender ────┼──► HirschRelay
                │                                         │     │
                │  VelocitySreHealthWorker  ──┐           │     ▼
                │   ├─ WindowsServiceHealth   │           │  HirschNotifyMobile
                │   └─ SdkHealth              │           │
                │                             ▼           │
                │                   IHealthEventEmitter ──┘
                └─────────────────────────────────────────┘
```

Two ingest paths feed the same `IEventProcessor`:

- **VelocityAdapter mode** — runs locally on the Velocity server, uses the
  `VelocityAdapter.dll` SDK to subscribe to live events via TCP and SQL.
- **WebSocket mode** — runs anywhere, receives events forwarded by another
  HirschNotify instance over an authenticated WebSocket.

A third subsystem — the **SRE health framework** — runs alongside in
VelocityAdapter mode and emits Velocity stack health (Windows services, SDK
backlog, SQL latency, failed-login rates) through the same event pipeline so
operators can build filter rules against those signals just like access events.

---

## Prerequisites

| Requirement | Notes |
|---|---|
| **.NET 10 SDK** | Project targets `net10.0`. Install from <https://dotnet.microsoft.com/download/dotnet/10.0>, or run `mise install` from the repo root to use the version pinned in `mise.toml`. |
| **Windows** | Required for VelocityAdapter mode and the SRE health framework's Windows-specific sources. WebSocket mode runs on macOS/Linux for development. |
| **Velocity install (optional)** | Required only when running in VelocityAdapter mode. Reads connection config from the Hirsch registry key. |
| **SQL Server (optional)** | Default DB provider is SQLite for dev. Switch to SQL Server via `DatabaseProvider` config. |

---

## Build & run

### From source (development)

```bash
# From the repo root (HirschNotify/HirschNotify):
dotnet restore
dotnet build
dotnet run
```

The web UI starts on `http://localhost:5100` by default (configurable in
`appsettings.json` under `Kestrel:Endpoints:Http:Url`).

In Development:

- The default DB provider is SQLite (`HirschNotify.db` in the working
  directory) — no SQL Server needed.
- `DevSeeder` populates the database with sample recipients and filter rules
  on first run.
- Code changes can be hot-reloaded with `dotnet watch run`.

### Publishing for deployment

```powershell
dotnet publish -c Release -r win-x64 --self-contained HirschNotify.csproj
```

Outputs a self-contained Windows binary suitable for the WiX installer to
package. See [Deployment](#deployment) below.

### Building the installer

WiX v5 is Windows-only. From the repo root in PowerShell:

```powershell
dotnet publish -c Release -r win-x64 --self-contained HirschNotify.csproj
dotnet build -c Release installer\HirschNotify.Installer.wixproj
# MSI lands at installer\bin\Release\HirschNotify-v1.0.msi
```

For full installer details — branding assets, dialog flow, GitHub Actions
release workflow — see [`installer/README.md`](installer/README.md).

---

## Configuration

HirschNotify uses two layers of configuration:

1. **`appsettings.json`** — bootstrap config: database, logging, web UI port,
   and the `Health` section. Loaded at startup, hot-reloaded for `IOptionsMonitor`
   bindings.
2. **`ISettingsService` (DB-backed)** — runtime config managed through the web
   UI: event source mode, Velocity connection overrides, recipients, filter
   rules, encrypted secrets. Stored in the same SQLite/SQL Server database.

### `appsettings.json` reference

```jsonc
{
  "DatabaseProvider": "Sqlite",            // or "SqlServer"
  "ConnectionStrings": {
    "DefaultConnection": "Data Source=HirschNotify.db"
  },
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft": "Warning",
        "Microsoft.Hosting.Lifetime": "Information"
      }
    }
  },
  "Kestrel": {
    "Endpoints": {
      "Http": { "Url": "http://localhost:5100" }
    }
  },
  "Health": {
    "PollIntervalSeconds": 30,
    "WindowsServices": {
      "Enabled": true,
      "MonitoredServices": [
        "ExServer",
        "DTServer",
        "MSSQL$*",
        "SDServer",
        "VWSX"
      ],
      "EmitSnapshots": false,
      "CriticalOnAutomaticStopped": true
    },
    "WindowsEventLog": {
      "Enabled": false,
      "Subscriptions": []
    },
    "Sdk": {
      "Enabled": true,
      "EmitSnapshots": false,
      "QueueWarnThreshold": 100,
      "QueueCriticalThreshold": 500,
      "FailedLoginWarnRatePerMinute": 5,
      "FailedLoginCriticalRatePerMinute": 20,
      "SqlLatencyWarnMs": 500,
      "SqlLatencyCriticalMs": 2000
    }
  }
}
```

#### `Health.WindowsServices.MonitoredServices`

Patterns supporting `*` and `?` glob wildcards. The defaults
(`ExServer`, `DTServer`, `MSSQL$*`, `SDServer`, `VWSX`) cover the core
Velocity stack — extend with extra integrations at your site. Patterns are
re-resolved against SCM on every poll, so newly-installed services that match
a pattern are picked up without restarting HirschNotify.

#### `Health.Sdk` thresholds

All thresholds are edge-triggered: an event fires only on a band transition
(Ok → Warning → Critical or back), not every poll while the metric is over the
line. SQL latency is measured by timing the `pendingDownloadQueueCount` SDK
call — there's no separate canary query.

### DB-backed settings (managed via web UI)

| Key | Purpose |
|---|---|
| `EventSource:Mode` | `WebSocket` or `VelocityAdapter`. Gates which ingest worker activates. |
| `Velocity:SqlServer` | Override the SQL Server resolved from the Velocity registry. |
| `Velocity:Database` | Override the Velocity database name. |
| Filter rules / recipients / groups | Created and edited via the web UI; persisted in EF Core entities. |
| Encrypted secrets | Stored via `ISettingsService.SetEncryptedAsync` (DPAPI on Windows). |

### Velocity registry (VelocityAdapter mode only)

`VelocityAdapterWorker` reads SQL Server / Database / Application Role from:

```
HKLM\SOFTWARE\Wow6432Node\Hirsch Electronics\Velocity\Client
```

…the standard Velocity client registry key written by the Velocity install.
Settings under the `Velocity:*` keys above override these values when present.

### Installer first-run hook

If `install-config.json` exists in the install directory at startup, its
values are merged into `ISettingsService` and the file is deleted. This is
how the WiX installer applies the user's chosen Event Source mode without
shipping a custom `appsettings.json`.

---

## Project layout

```
HirschNotify/
├── Program.cs                      Composition root + DI wiring
├── HirschNotify.csproj             net10.0, references VelocityAdapter.dll
├── appsettings.json                Bootstrap config + Health section
├── Data/                           EF Core DbContext, migrations, seeders
├── Models/                         FilterRule, Recipient, ThrottleState, etc.
├── Pages/                          Razor Pages web UI
├── Services/
│   ├── EventProcessor.cs           Pipeline: filter → throttle → notify
│   ├── FilterEngine.cs             Evaluates filter rules against an event
│   ├── ThrottleManager.cs          Per-rule × per-recipient rate limits
│   ├── NotificationSender.cs       Forwards to HirschRelay
│   ├── SettingsService.cs          DB-backed key/value config
│   ├── ConnectionState.cs          Live connection status + recent events
│   ├── EventSchema.cs              Field metadata for the rule editor UI
│   ├── IVelocityServerAccessor.cs  Singleton holder for the live VelocityServer
│   └── Health/                     SRE health framework (see below)
│       ├── HealthEvent.cs          Envelope DTO
│       ├── IHealthEventEmitter.cs  Choke point into the event pipeline
│       ├── HealthEventEmitter.cs
│       ├── IHealthSource.cs        Pluggable producer interface
│       ├── HealthSettings.cs       IOptionsMonitor-bound settings
│       ├── WildcardMatcher.cs      Glob helper for service-name patterns
│       └── Sources/
│           ├── WindowsServiceHealthSource.cs
│           └── SdkHealthSource.cs
├── Workers/
│   ├── VelocityAdapterWorker.cs    SDK → IEventProcessor (VelocityAdapter mode)
│   ├── WebSocketWorker.cs          Relay WS → IEventProcessor (WebSocket mode)
│   ├── VelocitySreHealthWorker.cs  Hosts IHealthSource instances
│   ├── ConnectionMonitorWorker.cs  Periodic health check on the upstream
│   ├── ThrottleCleanupWorker.cs    Expires stale throttle records
│   └── RelayHeartbeatWorker.cs     Keeps the relay channel alive
├── VelocityAdapter/                Vendor SDK DLL + dependencies (referenced)
└── installer/                      WiX v5 MSI installer
```

---

## Event flow

1. **Ingest** — `VelocityAdapterWorker` (SDK callbacks) or `WebSocketWorker`
   (relay frames) build a JSON envelope and call
   `IEventProcessor.ProcessEventAsync(json)`.
2. **Schema lookup** — `EventSchema` provides field metadata so the rule
   editor UI knows which fields each event source exposes.
3. **Filter evaluation** — `FilterEngine.EvaluateAsync` walks active
   `FilterRule` rows, comparing `FilterCondition`s against the event's JSON
   fields (case-insensitive path resolution, type-aware operators).
4. **Throttle check** — `ThrottleManager.ShouldSendAsync` enforces a
   per-rule × per-recipient sliding window (`ThrottleMaxSms` per
   `ThrottleWindowMinutes`).
5. **Dispatch** — `NotificationSender.SendAsync` renders the rule's message
   template and forwards to HirschRelay.
6. **Audit** — `ConnectionState.AddRecentEvent` retains the last 100 events
   for the dashboard.

The SRE health framework reuses **steps 2–6** verbatim — health events flow
through the same `IEventProcessor`, so filter rules can target
`source equals WindowsService` + `severity equals critical` exactly the same
way they target `source equals TransactionEvent`.

---

## SRE health framework

Adds operator-facing visibility into the Velocity stack itself, on top of the
access events HirschNotify already forwards. Active only when
`EventSource:Mode == VelocityAdapter`.

### Built-in sources

| Source | What it watches | Emits |
|---|---|---|
| **WindowsServiceHealth** | SCM state of services matching the configured patterns (defaults: `ExServer`, `DTServer`, `MSSQL$*`, `SDServer`, `VWSX`) | `state_change` events on transitions; optional `snapshot` events. Automatic services in `Stopped` state classified as Critical. |
| **SdkHealth** | `pendingDownloadQueueCount`, `HowManySQLFailedLoginsSince`, SQL round-trip latency | `queue_threshold`, `failed_logins`, `sql_latency` events when crossing warn/critical bands. Optional `snapshot` events. |

### How a health event reaches a phone

```
IHealthSource ──► IHealthEventEmitter ──► IEventProcessor
                        │
                        │ serializes HealthEvent into a flat JSON envelope
                        │ with source / category / severity / description /
                        │ + source-specific fields
                        ▼
                  FilterEngine ──► ThrottleManager ──► NotificationSender
```

Because health events serialize through the same envelope shape as Velocity
events, all the existing rule editor / throttle / recipient infrastructure
works unchanged.

### Adding a new health source

The framework is intentionally pluggable. To add (for example) a Windows
Event Log source:

1. **Implement `IHealthSource`** under `Services/Health/Sources/`:

   ```csharp
   public sealed class WindowsEventLogHealthSource : IHealthSource
   {
       public string Name => "WindowsEventLog";
       public bool IsEnabled => OperatingSystem.IsWindows() && _options.CurrentValue.WindowsEventLog.Enabled;

       public async Task RunAsync(IHealthEventEmitter emitter, CancellationToken ct)
       {
           // Subscribe to EventLogWatcher; for each matching record:
           await emitter.EmitAsync(new HealthEvent
           {
               Source = Name,
               Category = "log_record",
               Severity = HealthSeverity.Warning,
               Description = $"...",
               Fields = { ["providerName"] = ..., ["eventId"] = ..., ... },
           }, ct);
       }
   }
   ```

2. **Add a settings class** (or extend `WindowsEventLogHealthSettings`) in
   `Services/Health/HealthSettings.cs`. The `Health` section in
   `appsettings.json` already reserves a `WindowsEventLog` subsection.

3. **Register in `Program.cs`** alongside the existing sources:

   ```csharp
   if (OperatingSystem.IsWindows())
   {
       builder.Services.AddSingleton<IHealthSource, WindowsServiceHealthSource>();
       builder.Services.AddSingleton<IHealthSource, WindowsEventLogHealthSource>();
   }
   ```

4. **Add field metadata to `EventSchema.SourceFields`** so the rule editor UI
   shows the new fields:

   ```csharp
   ["WindowsEventLog"] = new()
   {
       new("providerName", "string", "Event log provider"),
       new("eventId",      "number", "Provider-specific event ID"),
       new("level",        "string", "Severity level"),
       // ...
   },
   ```

`VelocitySreHealthWorker` automatically picks the new source up from DI on
the next start, runs it on its own task, and restarts it on crash with a
backoff so one bad source can't take down the others.

### Sources that need the live VelocityServer

`SdkHealthSource` reaches the SDK through `IVelocityServerAccessor`, a
singleton holder that `VelocityAdapterWorker` populates on
`ConnectionSuccess` and clears on disconnect. Any new source that needs to
make SDK calls should inject `IVelocityServerAccessor` and follow the same
"check `Current?.IsConnected == true` before calling" pattern — never
construct a second `VelocityServer` instance, since each one consumes a
license seat.

---

## Deployment

### Production install (Windows)

Use the WiX MSI installer. It guides operators through:

1. Install directory
2. Web UI port (default 5100)
3. Event source mode (WebSocket or VelocityAdapter)
4. Service account credentials

…then registers `HirschNotify` as a Windows service (auto-start, restart on
failure), opens the firewall port, writes `install-config.json` so the
service applies the chosen mode on first startup, and starts the service.

For the full installer description, build instructions, and release workflow
see [`installer/README.md`](installer/README.md).

### Service identity

In VelocityAdapter mode the service account needs:

- **Read access** to `HKLM\SOFTWARE\Wow6432Node\Hirsch Electronics\Velocity\Client`
- **Network access** to the Velocity SQL Server
- **Local rights** to query SCM (for `WindowsServiceHealthSource`)
- **Application Role password** to decrypt with `ConnectDecrypt` (provided by
  the registry / DPAPI on the Velocity host)

For SRE event log monitoring (when added), the account also needs membership
in **Event Log Readers**.

### Health framework deployment notes

- Health monitoring activates automatically when `EventSource:Mode` is
  `VelocityAdapter`. No extra install steps.
- Default thresholds are tuned for typical mid-size deployments — review and
  adjust `Health.Sdk.*` values for sites with unusually large credential
  populations or strict failed-login policies.
- Monitored service patterns should be reviewed per-site — extra integrations
  (Identiv, third-party gateways) commonly install their own services that
  SREs want covered.

---

## Logging

Serilog writes to:

- **Console** (always)
- **`Logs/HirschNotify-yyyyMMdd.log`** (rolling daily file)

When running as a Windows service, `Logs/` is relative to the install
directory (e.g. `C:\Program Files\HirschNotify\Logs\`).

Log levels are configured under `Serilog.MinimumLevel` in `appsettings.json`.
Health framework activity logs at the `HirschNotify.Services.Health.*` and
`HirschNotify.Workers.VelocitySreHealthWorker` categories — set those to
`Debug` to see per-poll output.

---

## Related projects

| Repo | Purpose |
|---|---|
| **HirschNotify** *(this repo)* | The Windows service / web UI |
| **HirschRelay** | Cloud relay that bridges HirschNotify to push notifications |
| **HirschNotifyMobile** | Flutter mobile app that receives notifications |
| **VelocityAdapter SDK** | Vendor SDK referenced by `HirschNotify.csproj` (DLL shipped under `VelocityAdapter/`) |
