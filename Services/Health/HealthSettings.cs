namespace HirschNotify.Services.Health;

/// <summary>
/// Strongly-typed binding for the <c>Health</c> section of appsettings.json.
/// Exposed through <c>IOptionsMonitor&lt;HealthSettings&gt;</c> so sources pick
/// up config changes without a restart.
/// </summary>
public sealed class HealthSettings
{
    public const string SectionName = "Health";

    /// <summary>
    /// Default poll cadence for sources that poll. Individual sources may override.
    /// </summary>
    public int PollIntervalSeconds { get; set; } = 30;

    public WindowsServiceHealthSettings WindowsServices { get; set; } = new();

    public WindowsEventLogHealthSettings WindowsEventLog { get; set; } = new();

    public SdkHealthSettings Sdk { get; set; } = new();
}

public sealed class WindowsServiceHealthSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Service names to monitor. Supports <c>*</c> and <c>?</c> glob wildcards —
    /// e.g. <c>MSSQL$*</c> matches any SQL Server instance. Non-wildcard entries
    /// are matched case-insensitively against the Windows service short name.
    /// </summary>
    public List<string> MonitoredServices { get; set; } = new()
    {
        "ExServer",
        "DTServer",
        "MSSQL$*",
        "SDServer",
        "VWSX",
    };

    /// <summary>Override <see cref="HealthSettings.PollIntervalSeconds"/> for this source.</summary>
    public int? PollIntervalSeconds { get; set; }

    /// <summary>
    /// Emit a <c>snapshot</c> event every poll cycle in addition to edge-triggered
    /// state-change events. Off by default to avoid flooding the feed.
    /// </summary>
    public bool EmitSnapshots { get; set; } = false;

    /// <summary>
    /// Elevate severity to <see cref="HealthSeverity.Critical"/> when a service whose
    /// start type is Automatic is observed in the Stopped state.
    /// </summary>
    public bool CriticalOnAutomaticStopped { get; set; } = true;
}

/// <summary>
/// Placeholder for the forthcoming Windows Event Log source. Filled in when the
/// event-log source is implemented — carved out now so the options tree stays
/// stable across the framework build.
/// </summary>
public sealed class WindowsEventLogHealthSettings
{
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Logs to subscribe to. Each entry names a log (e.g. "Application", "System")
    /// and the providers / minimum level to accept.
    /// </summary>
    public List<WindowsEventLogSubscription> Subscriptions { get; set; } = new();
}

public sealed class WindowsEventLogSubscription
{
    public string LogName { get; set; } = "Application";
    public List<string> Providers { get; set; } = new();
    public string MinLevel { get; set; } = "Warning";
}

/// <summary>
/// Thresholds for the Velocity SDK health source — credential download backlog,
/// failed SQL login rate, and SQL round-trip latency (approximated by the timing
/// of the backlog query).
/// </summary>
public sealed class SdkHealthSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Override <see cref="HealthSettings.PollIntervalSeconds"/> for this source.</summary>
    public int? PollIntervalSeconds { get; set; }

    /// <summary>
    /// Emit a <c>snapshot</c> event every poll cycle in addition to threshold-crossing
    /// events. Off by default — snapshots are better consumed as gauges.
    /// </summary>
    public bool EmitSnapshots { get; set; } = false;

    /// <summary>
    /// Credential download backlog thresholds. A rising queue means the host service
    /// is behind or a controller is unreachable.
    /// </summary>
    public int QueueWarnThreshold { get; set; } = 100;
    public int QueueCriticalThreshold { get; set; } = 500;

    /// <summary>
    /// SQL round-trip latency thresholds (milliseconds), measured by timing the
    /// <c>pendingDownloadQueueCount</c> query.
    /// </summary>
    public int SqlLatencyWarnMs { get; set; } = 500;
    public int SqlLatencyCriticalMs { get; set; } = 2000;
}
