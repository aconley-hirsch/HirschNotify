using System.Diagnostics;
using Microsoft.Extensions.Options;
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
/// <item><description><c>snapshot</c> — optional periodic gauge dump when <see cref="SdkHealthSettings.EmitSnapshots"/> is on</description></item>
/// </list>
/// Threshold events are edge-triggered: they only fire when the metric crosses
/// the threshold band, not on every poll while over the line. Rearm once the
/// metric drops back below the warn threshold.
/// </remarks>
public sealed class SdkHealthSource : IHealthSource
{
    public string Name => "SdkHealth";

    public bool IsEnabled => _options.CurrentValue.Sdk.Enabled;

    private readonly IVelocityServerAccessor _serverAccessor;
    private readonly IOptionsMonitor<HealthSettings> _options;
    private readonly ILogger<SdkHealthSource> _logger;

    private Band _queueBand = Band.Ok;
    private Band _latencyBand = Band.Ok;

    public SdkHealthSource(
        IVelocityServerAccessor serverAccessor,
        IOptionsMonitor<HealthSettings> options,
        ILogger<SdkHealthSource> logger)
    {
        _serverAccessor = serverAccessor;
        _options = options;
        _logger = logger;
    }

    public async Task RunAsync(IHealthEventEmitter emitter, CancellationToken cancellationToken)
    {
        _logger.LogInformation("SdkHealthSource starting");

        while (!cancellationToken.IsCancellationRequested)
        {
            var settings = _options.CurrentValue;
            var sdkSettings = settings.Sdk;

            var server = _serverAccessor.Current;
            if (server is null || !server.IsConnected)
            {
                // VelocityAdapter isn't connected yet (or has dropped). Don't
                // emit noise; just wait for it. Existing connection events are
                // already published by VelocityAdapterWorker.
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
                    // Any SDK call can throw if the underlying SQL pool blipped;
                    // log and continue — the next poll will retry.
                    _logger.LogWarning(ex, "SdkHealthSource poll failed");
                }
            }

            var intervalSeconds = sdkSettings.PollIntervalSeconds ?? settings.PollIntervalSeconds;
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

        if (settings.EmitSnapshots)
        {
            await emitter.EmitAsync(new HealthEvent
            {
                Source = Name,
                Category = "snapshot",
                Severity = HealthSeverity.Info,
                Description = $"SDK snapshot: queue={queueCount}, sqlLatencyMs={latencyMs}",
                Fields =
                {
                    ["queueCount"] = queueCount,
                    ["sqlLatencyMs"] = latencyMs,
                    ["velocityRelease"] = SafeGet(() => server.VelocityRelease),
                    ["serverName"] = SafeGet(() => server.ServerName),
                    ["database"] = SafeGet(() => server.Database),
                },
            }, cancellationToken);
        }
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
