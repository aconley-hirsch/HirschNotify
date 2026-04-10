using HirschNotify.Services;
using HirschNotify.Services.Health;

namespace HirschNotify.Workers;

/// <summary>
/// Host for <see cref="IHealthSource"/> instances. Only active when the adapter
/// is in VelocityAdapter mode — in WebSocket mode the SRE health surface is not
/// relevant because HirschNotify isn't local to the Velocity server.
/// </summary>
/// <remarks>
/// Each enabled source runs on its own long-lived task. If a source throws, it
/// is logged and restarted after a short backoff so one misbehaving source can't
/// take down the whole worker.
/// </remarks>
public sealed class VelocitySreHealthWorker : BackgroundService
{
    private static readonly TimeSpan SourceRestartBackoff = TimeSpan.FromSeconds(10);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEnumerable<IHealthSource> _sources;
    private readonly IHealthEventEmitter _emitter;
    private readonly ILogger<VelocitySreHealthWorker> _logger;

    public VelocitySreHealthWorker(
        IServiceScopeFactory scopeFactory,
        IEnumerable<IHealthSource> sources,
        IHealthEventEmitter emitter,
        ILogger<VelocitySreHealthWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _sources = sources;
        _emitter = emitter;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Give the host a moment to finish wiring (matches VelocityAdapterWorker).
            await Task.Delay(2000, stoppingToken);

            if (!await IsVelocityAdapterModeAsync(stoppingToken))
            {
                _logger.LogInformation(
                    "Event source is not VelocityAdapter — SRE health worker disabled");
                return;
            }

            var activeSources = _sources.Where(s => s.IsEnabled).ToList();
            if (activeSources.Count == 0)
            {
                _logger.LogInformation("No enabled health sources — SRE health worker idle");
                return;
            }

            _logger.LogInformation(
                "VelocitySreHealthWorker starting with sources: {Sources}",
                string.Join(", ", activeSources.Select(s => s.Name)));

            var runners = activeSources
                .Select(source => RunSourceAsync(source, stoppingToken))
                .ToArray();

            await Task.WhenAll(runners);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task RunSourceAsync(IHealthSource source, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await source.RunAsync(_emitter, stoppingToken);
                // Source returned cleanly; if we're still running, loop and restart.
                if (stoppingToken.IsCancellationRequested) break;
                _logger.LogWarning("Health source {Source} returned unexpectedly — restarting", source.Name);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Health source {Source} crashed — restarting after backoff", source.Name);
            }

            try
            {
                await Task.Delay(SourceRestartBackoff, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task<bool> IsVelocityAdapterModeAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var mode = await settings.GetAsync("EventSource:Mode") ?? "WebSocket";
        return !string.Equals(mode, "WebSocket", StringComparison.OrdinalIgnoreCase);
    }
}
