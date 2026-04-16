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
    public const int DefaultQueueWarnThreshold = 100;
    public const int DefaultQueueCriticalThreshold = 500;
    public const int DefaultSqlLatencyWarnMs = 500;
    public const int DefaultSqlLatencyCriticalMs = 2000;

    public bool Enabled { get; set; } = true;

    public int? PollIntervalSeconds { get; set; }

    public int QueueWarnThreshold { get; set; } = DefaultQueueWarnThreshold;
    public int QueueCriticalThreshold { get; set; } = DefaultQueueCriticalThreshold;

    public int SqlLatencyWarnMs { get; set; } = DefaultSqlLatencyWarnMs;
    public int SqlLatencyCriticalMs { get; set; } = DefaultSqlLatencyCriticalMs;
}
