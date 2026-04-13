using HirschNotify.Models;

namespace HirschNotify.Services;

public class NotificationSender : INotificationSender
{
    private readonly IRelaySender _relaySender;
    private readonly ISettingsService _settings;
    private readonly ILogger<NotificationSender> _logger;
    private readonly Dictionary<string, IContactMethodSender> _senders;

    public NotificationSender(
        IRelaySender relaySender,
        ISettingsService settings,
        IEnumerable<IContactMethodSender> senders,
        ILogger<NotificationSender> logger)
    {
        _relaySender = relaySender;
        _settings = settings;
        _logger = logger;
        _senders = senders.ToDictionary(s => s.Type, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<bool> SendAsync(Recipient recipient, string message)
    {
        var anySent = false;

        // Push via relay (built-in, always attempted)
        var registered = await _settings.GetAsync("Relay:Registered");
        if (registered == "true")
        {
            var pushSent = await _relaySender.SendAsync("Alert", message, recipientId: recipient.Id);
            if (pushSent) anySent = true;
        }
        else
        {
            _logger.LogWarning("Relay not registered, skipping push for {Recipient}", recipient.Name);
        }

        // Contact methods
        var contactMethods = recipient.ContactMethods?.Where(cm => cm.IsActive) ?? [];
        foreach (var cm in contactMethods)
        {
            // Check if this contact method type is enabled globally
            var enabled = await _settings.GetAsync($"ContactMethod:{cm.Type}:Enabled");
            if (enabled != "true")
            {
                _logger.LogDebug("Contact method type '{Type}' is not enabled, skipping", cm.Type);
                continue;
            }

            if (_senders.TryGetValue(cm.Type, out var sender))
            {
                try
                {
                    var sent = await sender.SendAsync(cm, "Alert", message);
                    if (sent) anySent = true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ContactMethod {Type}:{Id} failed for {Recipient}",
                        cm.Type, cm.Id, recipient.Name);
                }
            }
            else
            {
                _logger.LogWarning("No sender registered for contact method type '{Type}'", cm.Type);
            }
        }

        return anySent;
    }
}
