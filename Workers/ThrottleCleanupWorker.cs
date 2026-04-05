using EventAlertService.Data;
using Microsoft.EntityFrameworkCore;

namespace EventAlertService.Workers;

public class ThrottleCleanupWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ThrottleCleanupWorker> _logger;

    public ThrottleCleanupWorker(IServiceScopeFactory scopeFactory, ILogger<ThrottleCleanupWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                // Delete throttle states older than 1 hour (well past any reasonable window)
                var cutoff = DateTime.UtcNow.AddHours(-1);
                var deleted = await db.ThrottleStates
                    .Where(t => t.WindowStart < cutoff)
                    .ExecuteDeleteAsync(stoppingToken);

                if (deleted > 0)
                    _logger.LogInformation("Cleaned up {Count} expired throttle states", deleted);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during throttle cleanup");
            }
        }
    }
}
