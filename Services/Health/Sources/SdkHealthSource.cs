using System.Diagnostics;
using VelocityAdapter;

namespace HirschNotify.Services.Health.Sources;

/// <summary>
/// Polls the Velocity SDK for credential-backlog and SQL-latency signals. Reads
/// the currently-connected <see cref="VelocityServer"/> via
/// <see cref="IVelocityServerAccessor"/> — if no connection is live, the source
/// sleeps until one is established rather than erroring out.
/// </summary>
/// <remarks>
/// Signals emitted:
/// <list type="bullet">
/// <item><description><c>queue_threshold</c> — credential download queue crossed a warn/critical threshold</description></item>
/// <item><description><c>sql_latency</c> — SQL round-trip exceeded a threshold (measured by timing the backlog query)</description></item>
/// </list>
/// Threshold events are edge-triggered: they only fire when the metric crosses
/// the threshold band, not on every poll while over the line. Rearm once the
/// metric drops back below the warn threshold.
/// </remarks>
public sealed class SdkHealthSource : IHealthSource
{
    public string Name => "SdkHealth";

    public bool IsEnabled => true;

    private readonly IVelocityServerAccessor _serverAccessor;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SdkHealthSource> _logger;

    private Band _queueBand = Band.Ok;
    private Band _latencyBand = Band.Ok;

    public SdkHealthSource(
        IVelocityServerAccessor serverAccessor,
        IServiceScopeFactory scopeFactory,
        ILogger<SdkHealthSource> logger)
    {
        _serverAccessor = serverAccessor;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task RunAsync(IHealthEventEmitter emitter, CancellationToken cancellationToken)
    {
        _logger.LogInformation("SdkHealthSource starting");

        while (!cancellationToken.IsCancellationRequested)
        {
            var sdkSettings = await ResolveEffectiveAsync();

            var server = _serverAccessor.Current;
            if (server is null || !server.IsConnected)
            {
                _logger.LogDebug("SdkHealthSource idle — no live VelocityServer");
            }
            else
            {
                try
                {
                    await PollOnceAsync(server, emitter, sdkSettings, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "SdkHealthSource poll failed");
                }
            }

            var intervalSeconds = sdkSettings.PollIntervalSeconds ?? 30;
            if (intervalSeconds <= 0) intervalSeconds = 30;

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task PollOnceAsync(
        VelocityServer server,
        IHealthEventEmitter emitter,
        SdkHealthSettings settings,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        int queueCount;
        try
        {
            queueCount = server.pendingDownloadQueueCount();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "pendingDownloadQueueCount failed");
            return;
        }
        stopwatch.Stop();
        var latencyMs = (int)stopwatch.ElapsedMilliseconds;

        await CheckQueueThresholdAsync(emitter, settings, queueCount, cancellationToken);
        await CheckLatencyThresholdAsync(emitter, settings, latencyMs, cancellationToken);
    }

    private async Task CheckQueueThresholdAsync(
        IHealthEventEmitter emitter,
        SdkHealthSettings settings,
        int queueCount,
        CancellationToken cancellationToken)
    {
        var band = ClassifyBand(queueCount, settings.QueueWarnThreshold, settings.QueueCriticalThreshold);
        if (band == _queueBand) return;

        var severity = band switch
        {
            Band.Critical => HealthSeverity.Critical,
            Band.Warning => HealthSeverity.Warning,
            _ => HealthSeverity.Info,
        };

        await emitter.EmitAsync(new HealthEvent
        {
            Source = Name,
            Category = "queue_threshold",
            Severity = severity,
            Description = band == Band.Ok
                ? $"Credential download queue recovered (count={queueCount})"
                : $"Credential download queue crossed {band} threshold (count={queueCount})",
            Fields =
            {
                ["queueCount"] = queueCount,
                ["previousBand"] = _queueBand.ToString(),
                ["currentBand"] = band.ToString(),
                ["warnThreshold"] = settings.QueueWarnThreshold,
                ["criticalThreshold"] = settings.QueueCriticalThreshold,
            },
        }, cancellationToken);

        _queueBand = band;
    }

    private async Task CheckLatencyThresholdAsync(
        IHealthEventEmitter emitter,
        SdkHealthSettings settings,
        int latencyMs,
        CancellationToken cancellationToken)
    {
        var band = ClassifyBand(latencyMs, settings.SqlLatencyWarnMs, settings.SqlLatencyCriticalMs);
        if (band == _latencyBand) return;

        var severity = band switch
        {
            Band.Critical => HealthSeverity.Critical,
            Band.Warning => HealthSeverity.Warning,
            _ => HealthSeverity.Info,
        };

        await emitter.EmitAsync(new HealthEvent
        {
            Source = Name,
            Category = "sql_latency",
            Severity = severity,
            Description = band == Band.Ok
                ? $"SQL round-trip recovered ({latencyMs} ms)"
                : $"SQL round-trip crossed {band} threshold ({latencyMs} ms)",
            Fields =
            {
                ["sqlLatencyMs"] = latencyMs,
                ["previousBand"] = _latencyBand.ToString(),
                ["currentBand"] = band.ToString(),
                ["warnThresholdMs"] = settings.SqlLatencyWarnMs,
                ["criticalThresholdMs"] = settings.SqlLatencyCriticalMs,
            },
        }, cancellationToken);

        _latencyBand = band;
    }

    /// <summary>
    /// Classify a metric into one of three bands. Hysteresis is implicit: we
    /// only emit on transitions, so a metric hovering at the threshold emits
    /// once on the way up and once on the way down, not every poll.
    /// </summary>
    private async Task<SdkHealthSettings> ResolveEffectiveAsync()
    {
        var effective = new SdkHealthSettings();

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        var qw = await db.GetAsync("Health:Sdk:QueueWarnThreshold");
        if (int.TryParse(qw, out var qwVal)) effective.QueueWarnThreshold = qwVal;

        var qc = await db.GetAsync("Health:Sdk:QueueCriticalThreshold");
        if (int.TryParse(qc, out var qcVal)) effective.QueueCriticalThreshold = qcVal;

        var lw = await db.GetAsync("Health:Sdk:SqlLatencyWarnMs");
        if (int.TryParse(lw, out var lwVal)) effective.SqlLatencyWarnMs = lwVal;

        var lc = await db.GetAsync("Health:Sdk:SqlLatencyCriticalMs");
        if (int.TryParse(lc, out var lcVal)) effective.SqlLatencyCriticalMs = lcVal;

        var pi = await db.GetAsync("Health:Sdk:PollIntervalSeconds");
        if (int.TryParse(pi, out var piVal) && piVal > 0) effective.PollIntervalSeconds = piVal;

        return effective;
    }

    private static Band ClassifyBand(double value, double warnThreshold, double criticalThreshold)
    {
        if (value >= criticalThreshold) return Band.Critical;
        if (value >= warnThreshold) return Band.Warning;
        return Band.Ok;
    }

    private static string? SafeGet(Func<string?> getter)
    {
        try { return getter(); }
        catch { return null; }
    }

    private enum Band { Ok, Warning, Critical }
}
