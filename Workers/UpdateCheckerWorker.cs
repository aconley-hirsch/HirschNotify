using HirschNotify.Services;

namespace HirschNotify.Workers;

/// <summary>
/// Polls HirschRelay for new HirschNotify releases on a fixed 6-hour
/// interval. Writes the result into <see cref="UpdateState"/> so the UI
/// banner and Settings &gt; About section can render synchronously.
/// </summary>
public sealed class UpdateCheckerWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly UpdateState _state;
    private readonly ILogger<UpdateCheckerWorker> _logger;

    public UpdateCheckerWorker(
        IServiceScopeFactory scopeFactory,
        UpdateState state,
        ILogger<UpdateCheckerWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _state = state;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Update check failed");
                _state.SetError(ex.Message);
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        // Resolve the checker from a fresh scope so transient HTTP clients
        // and scoped settings services are cleaned up between polls.
        using var scope = _scopeFactory.CreateScope();
        var checker = scope.ServiceProvider.GetRequiredService<IUpdateChecker>();

        var manifest = await checker.CheckAsync(ct);
        if (manifest is null)
        {
            _state.SetError("Relay not registered or unreachable.");
            return;
        }

        _state.SetSuccess(manifest);
        if (_state.IsUpdateAvailable())
        {
            _logger.LogInformation(
                "Update available: {Version} (current {Current})",
                manifest.Version,
                UpdateState.CurrentVersion);
        }
    }
}
