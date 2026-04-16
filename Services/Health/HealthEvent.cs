namespace HirschNotify.Services.Health;

/// <summary>
/// Severity of a <see cref="HealthEvent"/>. Maps to filter/alerting tiers in
/// the existing notification pipeline.
/// </summary>
public enum HealthSeverity
{
    Info,
    Warning,
    Critical,
}

/// <summary>
/// Envelope for a single observation emitted by an <see cref="IHealthSource"/>.
/// Health events flow through <see cref="IHealthEventEmitter"/> → <see cref="IEventProcessor"/>
/// so they are subject to the same filter-rule / throttle / notification path as
/// Velocity access events. Sources should populate <see cref="Fields"/> with
/// anything a filter rule might want to target.
/// </summary>
public sealed record HealthEvent
{
    /// <summary>Logical source name (e.g. "WindowsService", "WindowsEventLog").</summary>
    public required string Source { get; init; }

    /// <summary>Sub-category within the source (e.g. "state_change", "queue_threshold").</summary>
    public required string Category { get; init; }

    public HealthSeverity Severity { get; init; } = HealthSeverity.Info;

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>Short, human-readable summary. Used for previews and SMS bodies.</summary>
    public required string Description { get; init; }

    /// <summary>
    /// Source-specific fields. Keys should be stable — they become the field paths
    /// filter rules reference. Values should be JSON-primitive-friendly.
    /// </summary>
    public Dictionary<string, object?> Fields { get; init; } = new();
}
