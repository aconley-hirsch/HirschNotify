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
    private readonly EventSourceModeSignal _modeSignal;
    private readonly ILogger<VelocitySreHealthWorker> _logger;

    public VelocitySreHealthWorker(
        IServiceScopeFactory scopeFactory,
        IEnumerable<IHealthSource> sources,
        IHealthEventEmitter emitter,
        EventSourceModeSignal modeSignal,
        ILogger<VelocitySreHealthWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _sources = sources;
        _emitter = emitter;
        _modeSignal = modeSignal;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Give the host a moment to finish wiring (matches VelocityAdapterWorker).
            await Task.Delay(2000, stoppingToken);

            var activeSources = _sources.Where(s => s.IsEnabled).ToList();
            if (activeSources.Count == 0)
            {
                _logger.LogInformation("No enabled health sources — SRE health worker idle");
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                // Re-check mode on every pass so flipping EventSource:Mode
                // in Settings wakes the worker without a service restart.
                if (!await IsVelocityAdapterModeAsync(stoppingToken))
                {
                    await WaitForModeChangeOrStopAsync(stoppingToken);
                    continue;
                }

                _logger.LogInformation(
                    "VelocitySreHealthWorker starting with sources: {Sources}",
                    string.Join(", ", activeSources.Select(s => s.Name)));

                // Linking to _modeSignal.Token means a mode flip back to
                // WebSocket cancels every RunSourceAsync loop at once — the
                // Task.WhenAll then completes and we fall through to the top
                // of the outer loop to idle on the next signal.
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    stoppingToken, _modeSignal.Token);

                var runners = activeSources
                    .Select(source => RunSourceAsync(source, linkedCts.Token))
                    .ToArray();

                await Task.WhenAll(runners);

                if (stoppingToken.IsCancellationRequested) break;

                _logger.LogInformation("EventSource mode change — SRE health worker yielding");
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    // Sleep until either the service shuts down or SettingsModel bumps
    // the mode signal. Returning re-enters the outer loop, which re-reads
    // EventSource:Mode and decides whether this worker is now active.
    private async Task WaitForModeChangeOrStopAsync(CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _modeSignal.Token);
        try
        {
            await Task.Delay(Timeout.Infinite, linked.Token);
        }
        catch (OperationCanceledException)
        {
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
