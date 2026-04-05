using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EventAlertService.Models;

namespace EventAlertService.Services;

public class EventProcessor : IEventProcessor
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConnectionState _connectionState;
    private readonly ILogger<EventProcessor> _logger;

    public EventProcessor(IServiceScopeFactory scopeFactory, ConnectionState connectionState, ILogger<EventProcessor> logger)
    {
        _scopeFactory = scopeFactory;
        _connectionState = connectionState;
        _logger = logger;
    }

    public async Task ProcessEventAsync(string jsonMessage, CancellationToken stoppingToken = default)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonMessage);
            await ProcessEventAsync(doc.RootElement, jsonMessage, stoppingToken);
        }
        catch (JsonException)
        {
            _logger.LogInformation("Skipping non-JSON message: {Preview}",
                jsonMessage.Length > 100 ? jsonMessage[..100] + "..." : jsonMessage);
        }
    }

    public async Task ProcessEventAsync(JsonElement eventData, string rawJson, CancellationToken stoppingToken = default)
    {
        try
        {
            var recentEvent = new RecentEvent
            {
                Timestamp = DateTime.UtcNow,
                RawJson = rawJson,
                Preview = BuildEventPreview(eventData)
            };

            using var scope = _scopeFactory.CreateScope();
            var filterEngine = scope.ServiceProvider.GetRequiredService<IFilterEngine>();
            var throttleManager = scope.ServiceProvider.GetRequiredService<IThrottleManager>();
            var notificationSender = scope.ServiceProvider.GetRequiredService<INotificationSender>();

            var matchedRules = await filterEngine.EvaluateAsync(eventData);

            if (matchedRules.Count > 0)
            {
                recentEvent.MatchedRules = matchedRules.Select(r => r.Name).ToList();

                foreach (var rule in matchedRules)
                {
                    var directRecipients = rule.FilterRuleRecipients
                        .Where(fr => fr.Recipient.IsActive)
                        .Select(fr => fr.Recipient);

                    var groupRecipients = rule.FilterRuleRecipientGroups
                        .SelectMany(fg => fg.RecipientGroup.Members)
                        .Where(m => m.Recipient.IsActive)
                        .Select(m => m.Recipient);

                    var activeRecipients = directRecipients
                        .Concat(groupRecipients)
                        .DistinctBy(r => r.Id);

                    foreach (var recipient in activeRecipients)
                    {
                        var canSend = await throttleManager.ShouldSendAsync(
                            rule.Id, recipient.Id, rule.ThrottleMaxSms, rule.ThrottleWindowMinutes);

                        if (!canSend)
                        {
                            _logger.LogDebug("Throttled: Rule '{Rule}' to {Recipient}", rule.Name, recipient.Name);
                            continue;
                        }

                        var body = BuildSmsBody(rule, eventData);
                        var sent = await notificationSender.SendAsync(recipient, body);

                        if (sent)
                        {
                            recentEvent.AlertSent = true;
                            _connectionState.IncrementAlerts();
                        }
                    }
                }
            }

            _connectionState.AddRecentEvent(recentEvent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing event");
        }
    }

    internal static string BuildEventPreview(JsonElement eventData)
    {
        var parts = new List<string>();
        foreach (var prop in eventData.EnumerateObject().Take(4))
        {
            var val = prop.Value.ValueKind == JsonValueKind.String
                ? prop.Value.GetString() ?? ""
                : prop.Value.ToString();
            if (val.Length > 50) val = val[..47] + "...";
            parts.Add($"{prop.Name}: {val}");
        }
        return string.Join(" | ", parts);
    }

    internal static string BuildSmsBody(FilterRule rule, JsonElement eventData)
    {
        if (!string.IsNullOrWhiteSpace(rule.MessageTemplate))
            return RenderTemplate(rule.MessageTemplate, rule, eventData);

        var sb = new StringBuilder();
        sb.Append($"[{rule.Name}] Event matched\n");

        foreach (var condition in rule.Conditions.Take(3))
        {
            var value = ResolveFieldValue(eventData, condition.FieldPath);
            if (value != null)
                sb.Append($"{condition.FieldPath}: {value}\n");
        }

        sb.Append($"Time: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        return sb.ToString();
    }

    internal static string RenderTemplate(string template, FilterRule rule, JsonElement eventData)
    {
        var result = template;

        result = result.Replace("{ruleName}", rule.Name);
        result = result.Replace("{timestamp}", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
        result = result.Replace("{localtime}", DateTime.Now.ToString("M/d/yyyy h:mm:ss tt"));

        var regex = new Regex(@"\{([^}]+)\}");
        result = regex.Replace(result, match =>
        {
            var fieldPath = match.Groups[1].Value;
            if (fieldPath is "ruleName" or "timestamp" or "localtime")
                return match.Value;
            var value = ResolveFieldValue(eventData, fieldPath);
            return value ?? match.Value;
        });

        return result;
    }

    internal static string? ResolveFieldValue(JsonElement element, string path)
    {
        var segments = path.Split('.');
        var current = element;

        foreach (var segment in segments)
        {
            if (current.ValueKind != JsonValueKind.Object) return null;
            if (!TryGetPropertyIgnoreCase(current, segment, out var next)) return null;
            current = next;
        }

        return current.ToString();
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value)) return true;
        foreach (var prop in element.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }
        value = default;
        return false;
    }
}
