using EventAlertService.Services;

namespace EventAlertService.Workers;

public class RelayHeartbeatWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConnectionState _connectionState;
    private readonly ILogger<RelayHeartbeatWorker> _logger;

    public RelayHeartbeatWorker(IServiceScopeFactory scopeFactory, ConnectionState connectionState, ILogger<RelayHeartbeatWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _connectionState = connectionState;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for app to fully start before sending heartbeats
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();

                var registered = await settings.GetAsync("Relay:Registered");
                if (registered != "true")
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                    continue;
                }

                var relayClient = scope.ServiceProvider.GetRequiredService<IRelayClient>();
                var serviceName = await settings.GetAsync("Service:Name") ?? "";
                await relayClient.HeartbeatAsync(
                    serviceName,
                    _connectionState.Status.ToLowerInvariant(),
                    _connectionState.EventsReceived,
                    _connectionState.AlertsSentToday
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Relay heartbeat failed");
            }

            var intervalStr = "60";
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
                intervalStr = await settings.GetAsync("Relay:HeartbeatIntervalSec") ?? "60";
            }
            catch { /* use default */ }

            var interval = int.TryParse(intervalStr, out var sec) ? sec : 60;
            await Task.Delay(TimeSpan.FromSeconds(interval), stoppingToken);
        }
    }
}
