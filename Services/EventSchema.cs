namespace HirschNotify.Services;

public record FieldInfo(string Name, string Type, string Description);

/// <summary>
/// Static schema describing the fields emitted by each Velocity event type.
/// Derived from VelocityAdapterWorker's serialization of each event kind.
/// </summary>
public static class EventSchema
{
    /// <summary>Fields present on (most) event types — shown first in field pickers.</summary>
    public static readonly List<FieldInfo> CommonFields = new()
    {
        new("source",             "string",   "Event type (ExternalEvent, TransactionEvent, etc.)"),
        new("eventID",            "number",   "Unique event ID"),
        new("eventType",          "string",   "Event type code or name"),
        new("description",        "string",   "Human-readable description"),
        new("address",            "string",   "Device / door address (e.g. 1.1.1)"),
        new("velocityEventType",  "string",   "Velocity-specific event type name"),
        new("serverTime",         "datetime", "When the event was recorded on the server"),
        new("controllerTime",     "datetime", "When the event occurred on the controller"),
        new("severity",           "string",   "Health event severity (info / warning / critical)"),
        new("category",           "string",   "Health event sub-category (e.g. state_change)"),
        new("timestamp",          "datetime", "Event timestamp"),
    };

    /// <summary>Fields specific to each event source type.</summary>
    public static readonly Dictionary<string, List<FieldInfo>> SourceFields = new()
    {
        ["ExternalEvent"] = new()
        {
            new("controllerID",       "number",  "Controller ID"),
            new("alarmLevelPriority", "number",  "Alarm priority level"),
            new("reportAsAlarm",      "boolean", "Whether this event is reported as an alarm"),
            new("fromState",          "string",  "Previous state"),
            new("toState",            "string",  "New state"),
            new("portAddress",        "number",  "Port address"),
            new("serverID",           "number",  "Server ID"),
        },
        ["InternalEvent"] = new()
        {
            new("controllerID",       "number",  "Controller ID"),
            new("alarmLevelPriority", "number",  "Alarm priority level"),
            new("reportAsAlarm",      "boolean", "Whether this event is reported as an alarm"),
            new("fromState",          "string",  "Previous state"),
            new("portAddress",        "number",  "Port address"),
            new("serverID",           "number",  "Server ID"),
        },
        ["TransactionEvent"] = new()
        {
            new("controllerID",       "number",  "Controller ID"),
            new("alarmLevelPriority", "number",  "Alarm priority level"),
            new("reportAsAlarm",      "boolean", "Whether this event is reported as an alarm"),
            new("card",               "string",  "Card credential number"),
            new("pin",                "string",  "PIN credential"),
            new("disposition",        "string",  "Access granted / denied / etc."),
            new("transactionType",    "string",  "Transaction type name"),
            new("uid1",               "number",  "User ID 1"),
            new("uid2",               "number",  "User ID 2"),
            new("portAddress",        "number",  "Port address"),
            new("serverID",           "number",  "Server ID"),
        },
        ["SoftwareEvent"] = new()
        {
            new("timestamp",          "datetime", "Event timestamp"),
            new("sourceID",           "number",   "Source ID"),
            new("sourceName",         "string",   "Source name"),
            new("userID",             "number",   "User ID"),
            new("userName",           "string",   "User name"),
        },
        ["MiscEvent"] = new()
        {
            new("alarmLevelPriority", "number",  "Alarm priority level"),
            new("reportAsAlarm",      "boolean", "Whether this event is reported as an alarm"),
            new("portAddress",        "number",  "Port address"),
            new("serverID",           "number",  "Server ID"),
        },
        ["AlarmActive"] = new()
        {
            new("alarmId",            "number",  "Alarm ID"),
            new("domainId",           "number",  "Domain ID"),
            new("serverId",           "number",  "Server ID"),
            new("portAddress",        "number",  "Port address"),
        },
        ["AlarmAcknowledged"] = new()
        {
            new("alarmId",            "number",   "Alarm ID"),
            new("timestamp",          "datetime", "When the alarm was acknowledged"),
            new("operatorName",       "string",   "Operator who acknowledged"),
            new("workstation",        "string",   "Workstation name"),
        },
        ["AlarmCleared"] = new()
        {
            new("alarmId",            "number",   "Alarm ID"),
            new("timestamp",          "datetime", "When the alarm was cleared"),
            new("operatorName",       "string",   "Operator who cleared"),
            new("workstation",        "string",   "Workstation name"),
        },
        ["WindowsService"] = new()
        {
            new("serviceName",        "string",  "Windows service short name (e.g. DTServer)"),
            new("displayName",        "string",  "Windows service display name"),
            new("status",             "string",  "Current status (Running, Stopped, StartPending, etc.)"),
            new("previousStatus",     "string",  "Status before the observed transition (null on first observation)"),
            new("startMode",          "string",  "Start type (Automatic, Manual, Disabled, etc.)"),
            new("firstObservation",   "boolean", "True if this is the first time the service has been observed this session"),
        },
        ["SdkHealth"] = new()
        {
            new("queueCount",                "number",  "Pending credential downloads queued for controllers"),
            new("failedLoginCount",          "number",  "SQL failed logins observed since last poll"),
            new("failedLoginRatePerMinute",  "number",  "SQL failed login rate (per minute) over the last poll window"),
            new("sqlLatencyMs",              "number",  "Round-trip time (ms) of the backlog query — proxy for SQL health"),
            new("previousBand",              "string",  "Threshold band before this transition (Ok, Warning, Critical)"),
            new("currentBand",               "string",  "Threshold band after this transition (Ok, Warning, Critical)"),
            new("warnThreshold",             "number",  "Configured warn threshold for the queue metric"),
            new("criticalThreshold",         "number",  "Configured critical threshold for the queue metric"),
            new("warnRatePerMinute",         "number",  "Configured warn rate for failed logins"),
            new("criticalRatePerMinute",     "number",  "Configured critical rate for failed logins"),
            new("warnThresholdMs",           "number",  "Configured warn threshold for SQL latency"),
            new("criticalThresholdMs",       "number",  "Configured critical threshold for SQL latency"),
            new("velocityRelease",           "string",  "Velocity server release string (snapshot only)"),
            new("serverName",                "string",  "Connected server name (snapshot only)"),
            new("database",                  "string",  "Connected database name (snapshot only)"),
        },
    };

    public static string GetFieldType(string fieldPath)
    {
        foreach (var f in CommonFields)
            if (string.Equals(f.Name, fieldPath, StringComparison.OrdinalIgnoreCase)) return f.Type;
        foreach (var source in SourceFields.Values)
            foreach (var f in source)
                if (string.Equals(f.Name, fieldPath, StringComparison.OrdinalIgnoreCase)) return f.Type;
        return "string";
    }

    public static List<string> OperatorsForType(string type) => type switch
    {
        "number"   => new() { "equals", "not_equals", "greater_than", "less_than" },
        "boolean"  => new() { "equals" },
        "datetime" => new() { "greater_than", "less_than" },
        _          => new() { "equals", "not_equals", "contains" }
    };

    public static readonly Dictionary<string, string> OperatorLabels = new()
    {
        ["equals"]       = "is",
        ["not_equals"]   = "is not",
        ["contains"]     = "contains",
        ["greater_than"] = "is greater than",
        ["less_than"]    = "is less than",
    };
}
