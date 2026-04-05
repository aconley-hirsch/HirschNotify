using EventAlertService.Services;
using Microsoft.EntityFrameworkCore;

namespace EventAlertService.Workers;

public class ConnectionMonitorWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConnectionState _connectionState;
    private readonly ILogger<ConnectionMonitorWorker> _logger;
    private DateTime? _disconnectedSince;
    private bool _alertSent;

    public ConnectionMonitorWorker(IServiceScopeFactory scopeFactory, ConnectionState connectionState, ILogger<ConnectionMonitorWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _connectionState = connectionState;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(5000, stoppingToken);

            var status = _connectionState.Status;

            if (status == "Connected")
            {
                if (_disconnectedSince != null)
                {
                    _logger.LogInformation("WebSocket reconnected");
                    _disconnectedSince = null;
                    _alertSent = false;
                }
                continue;
            }

            // Track disconnection
            _disconnectedSince ??= DateTime.UtcNow;

            if (_alertSent) continue;

            using var scope = _scopeFactory.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();

            var thresholdStr = await settings.GetAsync("WebSocket:DisconnectAlertSec");
            if (!int.TryParse(thresholdStr, out var thresholdSec) || thresholdSec <= 0)
                thresholdSec = 120;

            var elapsed = DateTime.UtcNow - _disconnectedSince.Value;
            if (elapsed.TotalSeconds < thresholdSec) continue;

            // Send disconnect alert to all active recipients
            _logger.LogWarning("WebSocket disconnected for {Seconds}s, sending alerts", (int)elapsed.TotalSeconds);

            var db = scope.ServiceProvider.GetRequiredService<Data.AppDbContext>();
            var activeRecipients = await db.Recipients.Where(r => r.IsActive).ToListAsync(stoppingToken);

            var notificationSender = scope.ServiceProvider.GetRequiredService<INotificationSender>();
            var message = $"Alert: WebSocket connection lost for {(int)elapsed.TotalSeconds}s. Status: {status}. Reconnecting...";

            foreach (var recipient in activeRecipients)
            {
                await notificationSender.SendAsync(recipient, message);
            }

            _alertSent = true;
        }
    }
}
