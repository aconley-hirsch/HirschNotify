using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.ServiceProcess;

namespace HirschNotify.Services.Health.Sources;

/// <summary>
/// Polls Windows services matching the configured patterns and emits
/// <see cref="HealthEvent"/>s on state transitions (edge-triggered).
/// </summary>
/// <remarks>
/// Wildcard patterns are resolved against the current service list on every
/// poll so newly-installed services matching a pattern are picked up without a
/// restart. Patterns that match nothing are logged once per poll at debug level
/// so SREs can tell the difference between "service is down" and "you typoed
/// the pattern".
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsServiceHealthSource : IHealthSource
{
    public string Name => "WindowsService";

    public bool IsEnabled => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WindowsServiceHealthSource> _logger;

    /// <summary>
    /// Last observed state per service name, used to edge-trigger state-change
    /// events. Keyed by the resolved service short name, not the pattern.
    /// </summary>
    private readonly Dictionary<string, ObservedState> _lastSeen =
        new(StringComparer.OrdinalIgnoreCase);

    public WindowsServiceHealthSource(
        IServiceScopeFactory scopeFactory,
        ILogger<WindowsServiceHealthSource> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task RunAsync(IHealthEventEmitter emitter, CancellationToken cancellationToken)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _logger.LogInformation("WindowsServiceHealthSource skipped — not running on Windows");
            return;
        }

        _logger.LogInformation("WindowsServiceHealthSource starting");

        while (!cancellationToken.IsCancellationRequested)
        {
            var serviceSettings = await ResolveEffectiveAsync();

            try
            {
                await PollOnceAsync(emitter, serviceSettings, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WindowsServiceHealthSource poll failed");
            }

            var intervalSeconds = serviceSettings.PollIntervalSeconds ?? 30;
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

    /// <summary>
    /// Read all WindowsServices settings from the DB. A missing key falls
    /// back to the C# default on <see cref="WindowsServiceHealthSettings"/>;
    /// an explicitly-stored empty list (key present, value blank) means
    /// "monitor nothing" so the Health page's "uncheck everything" path
    /// actually takes effect.
    /// </summary>
    private async Task<WindowsServiceHealthSettings> ResolveEffectiveAsync()
    {
        var effective = new WindowsServiceHealthSettings();

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        var list = await db.GetAsync("Health:WindowsServices:MonitoredServices");
        if (list is not null)
        {
            effective.MonitoredServices = list
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();
        }

        var interval = await db.GetAsync("Health:WindowsServices:PollIntervalSeconds");
        if (int.TryParse(interval, out var i) && i > 0)
            effective.PollIntervalSeconds = i;

        var critical = await db.GetAsync("Health:WindowsServices:CriticalOnAutomaticStopped");
        if (bool.TryParse(critical, out var c))
            effective.CriticalOnAutomaticStopped = c;

        return effective;
    }

    private async Task PollOnceAsync(
        IHealthEventEmitter emitter,
        WindowsServiceHealthSettings settings,
        CancellationToken cancellationToken)
    {
        var patterns = settings.MonitoredServices;
        if (patterns.Count == 0) return;

        // ServiceController.GetServices() hits SCM once; reuse for all pattern matches.
        ServiceController[] allServices;
        try
        {
            allServices = ServiceController.GetServices();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to enumerate Windows services (insufficient privilege?)");
            return;
        }

        try
        {
            var matched = ResolvePatterns(patterns, allServices);

            foreach (var svc in matched.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await InspectServiceAsync(emitter, svc, settings, cancellationToken);
            }

            // Report patterns that matched nothing so misconfiguration is visible.
            foreach (var pattern in patterns)
            {
                if (!matched.Keys.Any(k => WildcardMatcher.IsMatch(pattern, k)))
                    _logger.LogDebug("Service pattern {Pattern} matched no installed services", pattern);
            }
        }
        finally
        {
            foreach (var svc in allServices)
                svc.Dispose();
        }
    }

    private async Task InspectServiceAsync(
        IHealthEventEmitter emitter,
        ServiceController svc,
        WindowsServiceHealthSettings settings,
        CancellationToken cancellationToken)
    {
        ServiceControllerStatus status;
        ServiceStartMode startMode;
        string displayName;
        try
        {
            svc.Refresh();
            status = svc.Status;
            startMode = svc.StartType;
            displayName = svc.DisplayName;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to read state for service {Service}", svc.ServiceName);
            return;
        }

        var previous = _lastSeen.TryGetValue(svc.ServiceName, out var prev) ? prev : (ObservedState?)null;
        var current = new ObservedState(status, startMode, DateTime.UtcNow);
        _lastSeen[svc.ServiceName] = current;

        var stateChanged = previous is null || previous.Value.Status != current.Status;

        if (stateChanged)
        {
            var severity = ClassifySeverity(current, settings);
            var description = previous is null
                ? $"Service {svc.ServiceName} observed as {status}"
                : $"Service {svc.ServiceName} {previous.Value.Status} → {status}";

            await emitter.EmitAsync(new HealthEvent
            {
                Source = Name,
                Category = "state_change",
                Severity = severity,
                Description = description,
                Fields =
                {
                    ["serviceName"] = svc.ServiceName,
                    ["displayName"] = displayName,
                    ["status"] = status.ToString(),
                    ["previousStatus"] = previous?.Status.ToString(),
                    ["startMode"] = startMode.ToString(),
                    ["firstObservation"] = previous is null,
                },
            }, cancellationToken);
        }

    }

    private static HealthSeverity ClassifySeverity(ObservedState state, WindowsServiceHealthSettings settings)
    {
        if (state.Status == ServiceControllerStatus.Running)
            return HealthSeverity.Info;

        if (state.Status == ServiceControllerStatus.Stopped)
        {
            if (settings.CriticalOnAutomaticStopped && state.StartMode == ServiceStartMode.Automatic)
                return HealthSeverity.Critical;
            return HealthSeverity.Warning;
        }

        // Pending states (StartPending / StopPending / PausePending / ContinuePending / Paused)
        return HealthSeverity.Warning;
    }

    /// <summary>
    /// Resolve each pattern to the concrete services it matches, de-duplicated by
    /// service name (a service can match multiple patterns).
    /// </summary>
    private static Dictionary<string, ServiceController> ResolvePatterns(
        IReadOnlyList<string> patterns,
        ServiceController[] allServices)
    {
        var result = new Dictionary<string, ServiceController>(StringComparer.OrdinalIgnoreCase);
        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;

            foreach (var svc in allServices)
            {
                if (WildcardMatcher.IsMatch(pattern, svc.ServiceName))
                    result.TryAdd(svc.ServiceName, svc);
            }
        }
        return result;
    }

    private readonly record struct ObservedState(
        ServiceControllerStatus Status,
        ServiceStartMode StartMode,
        DateTime ObservedAt);
}
