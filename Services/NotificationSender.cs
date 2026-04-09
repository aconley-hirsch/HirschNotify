using HirschNotify.Models;

namespace HirschNotify.Services;

public class NotificationSender : INotificationSender
{
    private readonly IRelaySender _relaySender;
    private readonly ISettingsService _settings;
    private readonly ILogger<NotificationSender> _logger;

    public NotificationSender(IRelaySender relaySender, ISettingsService settings, ILogger<NotificationSender> logger)
    {
        _relaySender = relaySender;
        _settings = settings;
        _logger = logger;
    }

    public async Task<bool> SendAsync(Recipient recipient, string message)
    {
        var registered = await _settings.GetAsync("Relay:Registered");
        if (registered != "true")
        {
            _logger.LogWarning("Relay not registered, skipping push for {Recipient}", recipient.Name);
            return false;
        }

        return await _relaySender.SendAsync("Alert", message, recipientId: recipient.Id);
    }
}
