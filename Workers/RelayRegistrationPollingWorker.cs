using HirschNotify.Services;

namespace HirschNotify.Workers;

/// <summary>
/// Background worker that drives the self-service relay registration flow.
/// While <c>Relay:RequestStatus == "pending"</c>, it polls the relay's
/// registration-request endpoint and, on approval, automatically trades
/// the issued <c>rt_*</c> token for an instance API key via the existing
/// <see cref="IRelayClient.RegisterAsync"/> path.
/// </summary>
/// <remarks>
/// Two-loop structure so the worker is cheap when there's no pending
/// request: an outer dormant loop wakes every 30 seconds to check status,
/// and an inner active loop polls every 5 seconds (the first 24 polls,
/// ~2 minutes) then backs off to 30 seconds. Server-side cleanup
/// expires stale requests after 24h, so the worker doesn't need its own
/// upper bound — it just observes <c>{status:"expired"}</c> and exits the
/// active loop.
/// </remarks>
public class RelayRegistrationPollingWorker : BackgroundService
{
    private static readonly TimeSpan FastPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SlowPollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DormantInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan NetworkErrorBackoff = TimeSpan.FromSeconds(10);
    private const int FastPollCount = 24;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RelayRegistrationPollingWorker> _logger;

    public RelayRegistrationPollingWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<RelayRegistrationPollingWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Same startup delay as RelayHeartbeatWorker — let the app finish wiring.
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await IsRequestPendingAsync(stoppingToken))
                    await RunActivePollLoopAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RelayRegistrationPollingWorker outer loop error");
            }

            try
            {
                await Task.Delay(DormantInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task<bool> IsRequestPendingAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var status = await settings.GetAsync("Relay:RequestStatus") ?? "";
        return status == "pending";
    }

    private async Task RunActivePollLoopAsync(CancellationToken stoppingToken)
    {
        var pollCount = 0;
        _logger.LogInformation("Polling relay for registration request status");

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = pollCount < FastPollCount ? FastPollInterval : SlowPollInterval;

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            pollCount++;

            try
            {
                var done = await PollOnceAsync(stoppingToken);
                if (done) return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Registration request poll failed; backing off");
                try
                {
                    await Task.Delay(NetworkErrorBackoff, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// One iteration of the active loop. Returns true when the loop should
    /// exit (any terminal state — registered, rejected, expired, cancelled).
    /// </summary>
    private async Task<bool> PollOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        var status = await settings.GetAsync("Relay:RequestStatus") ?? "";
        if (status != "pending")
            return true;

        var requestId = await settings.GetAsync("Relay:RequestId") ?? "";
        var requestSecret = await settings.GetEncryptedAsync("Relay:RequestSecret") ?? "";
        if (string.IsNullOrEmpty(requestId) || string.IsNullOrEmpty(requestSecret))
        {
            _logger.LogWarning("RequestStatus is pending but RequestId/RequestSecret are missing — clearing state");
            await ClearRequestKeysAsync(settings);
            return true;
        }

        var relayClient = scope.ServiceProvider.GetRequiredService<IRelayClient>();
        var result = await relayClient.PollRegistrationRequestAsync(requestId, requestSecret);

        switch (result.Status)
        {
            case "delivered":
                if (string.IsNullOrEmpty(result.RegistrationToken))
                {
                    // Server says delivered but didn't include the token — likely a
                    // duplicate read after a successful pickup. Treat as terminal.
                    _logger.LogInformation("Registration request {RequestId} already delivered", requestId);
                    await ClearRequestKeysAsync(settings);
                    return true;
                }
                await CompleteRegistrationAsync(scope, settings, relayClient, result.RegistrationToken!, ct);
                return true;

            case "approved":
                // Race: server says approved but the GET didn't atomically consume it.
                // Defensive — keep polling, the next call should consume it.
                return false;

            case "rejected":
                _logger.LogInformation("Registration request {RequestId} was rejected: {Reason}",
                    requestId, result.RejectionReason ?? "(no reason)");
                await settings.SetAsync("Relay:RequestStatus", "rejected");
                await settings.SetAsync("Relay:RejectionReason", result.RejectionReason ?? "(no reason provided)");
                await settings.SetAsync("Relay:RequestId", "");
                await settings.SetAsync("Relay:RequestSecret", "");
                return true;

            case "expired":
                _logger.LogInformation("Registration request {RequestId} expired before approval", requestId);
                await settings.SetAsync("Relay:RequestStatus", "expired");
                await settings.SetAsync("Relay:RejectionReason", "Request expired before approval.");
                await settings.SetAsync("Relay:RequestId", "");
                await settings.SetAsync("Relay:RequestSecret", "");
                return true;

            case "cancelled":
                _logger.LogInformation("Registration request {RequestId} cancelled", requestId);
                await ClearRequestKeysAsync(settings);
                return true;

            case "pending":
            default:
                return false;
        }
    }

    /// <summary>
    /// Trade the delivered rt_* token for an instance API key via the
    /// existing register endpoint. Persist the API key BEFORE flipping
    /// Registered or clearing request keys so a crash mid-write doesn't
    /// lose the credential.
    /// </summary>
    private async Task CompleteRegistrationAsync(
        IServiceScope scope,
        ISettingsService settings,
        IRelayClient relayClient,
        string registrationToken,
        CancellationToken ct)
    {
        var urlResolver = scope.ServiceProvider.GetRequiredService<RelayUrlResolver>();
        var relayUrl = await urlResolver.GetAsync();
        var serviceName = await settings.GetAsync("Service:Name") ?? "";

        try
        {
            var result = await relayClient.RegisterAsync(relayUrl, serviceName, HirschNotify.Services.UpdateState.CurrentVersion, registrationToken);

            // Persist API key first — if any subsequent step crashes, the next
            // startup at least has working credentials.
            await settings.SetEncryptedAsync("Relay:ApiKey", result.ApiKey);
            await settings.SetAsync("Relay:InstanceId", result.InstanceId);
            await settings.SetAsync("Relay:Url", relayUrl);
            await settings.SetAsync("Relay:Registered", "true");

            await ClearRequestKeysAsync(settings);

            _logger.LogInformation(
                "Relay registration completed via self-service flow: instance {InstanceId}",
                result.InstanceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to complete registration after token delivery");
            // Surface as a rejected state so the user can dismiss and retry.
            await settings.SetAsync("Relay:RequestStatus", "rejected");
            await settings.SetAsync("Relay:RejectionReason", $"Token issued but registration failed: {ex.Message}");
            await settings.SetAsync("Relay:RequestId", "");
            await settings.SetAsync("Relay:RequestSecret", "");
        }
    }

    private static async Task ClearRequestKeysAsync(ISettingsService settings)
    {
        await settings.SetAsync("Relay:RequestId", "");
        await settings.SetAsync("Relay:RequestSecret", "");
        await settings.SetAsync("Relay:RequestStatus", "");
        await settings.SetAsync("Relay:RequestedAt", "");
        await settings.SetAsync("Relay:RejectionReason", "");
    }
}
