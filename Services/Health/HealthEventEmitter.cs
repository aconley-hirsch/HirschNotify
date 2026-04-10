using System.Text.Json;

namespace HirschNotify.Services.Health;

public sealed class HealthEventEmitter : IHealthEventEmitter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly IEventProcessor _eventProcessor;
    private readonly ConnectionState _connectionState;
    private readonly ILogger<HealthEventEmitter> _logger;

    public HealthEventEmitter(
        IEventProcessor eventProcessor,
        ConnectionState connectionState,
        ILogger<HealthEventEmitter> logger)
    {
        _eventProcessor = eventProcessor;
        _connectionState = connectionState;
        _logger = logger;
    }

    public async Task EmitAsync(HealthEvent evt, CancellationToken cancellationToken = default)
    {
        // Flatten Fields into the top-level envelope so filter rules can reference
        // source-specific field paths directly (e.g. "serviceName", "status") the
        // same way they target transaction event fields today.
        var payload = new Dictionary<string, object?>(evt.Fields.Count + 6)
        {
            ["source"] = evt.Source,
            ["category"] = evt.Category,
            ["severity"] = evt.Severity.ToString().ToLowerInvariant(),
            ["timestamp"] = evt.Timestamp,
            ["description"] = evt.Description,
            ["velocityEventType"] = evt.Source,
        };

        foreach (var (key, value) in evt.Fields)
        {
            // Don't let a source accidentally clobber envelope keys.
            if (!payload.ContainsKey(key))
                payload[key] = value;
        }

        string json;
        try
        {
            json = JsonSerializer.Serialize(payload, SerializerOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to serialize health event from {Source}/{Category}", evt.Source, evt.Category);
            return;
        }

        _connectionState.IncrementEvents();
        _logger.LogDebug("Health event [{Severity}] {Source}/{Category}: {Description}",
            evt.Severity, evt.Source, evt.Category, evt.Description);

        await _eventProcessor.ProcessEventAsync(json, cancellationToken);
    }
}
