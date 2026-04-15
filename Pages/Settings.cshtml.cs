using System.Net.WebSockets;
using HirschNotify.Data;
using HirschNotify.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace HirschNotify.Pages;

[Authorize]
public class SettingsModel : PageModel
{
    private readonly ISettingsService _settings;
    private readonly INotificationSender _notificationSender;
    private readonly IWebSocketAuthService _authService;
    private readonly IRelayClient _relayClient;
    private readonly RelayUrlResolver _relayUrlResolver;
    private readonly IServiceAccountManager _serviceAccountManager;
    private readonly UpdateState _updateState;
    private readonly IUpdateChecker _updateChecker;
    private readonly AppDbContext _db;
    private readonly ILogger<SettingsModel> _logger;

    public SettingsModel(
        ISettingsService settings,
        INotificationSender notificationSender,
        IWebSocketAuthService authService,
        IRelayClient relayClient,
        RelayUrlResolver relayUrlResolver,
        IServiceAccountManager serviceAccountManager,
        UpdateState updateState,
        IUpdateChecker updateChecker,
        AppDbContext db,
        ILogger<SettingsModel> logger)
    {
        _settings = settings;
        _notificationSender = notificationSender;
        _authService = authService;
        _relayClient = relayClient;
        _relayUrlResolver = relayUrlResolver;
        _serviceAccountManager = serviceAccountManager;
        _updateState = updateState;
        _updateChecker = updateChecker;
        _db = db;
        _logger = logger;
    }

    public UpdateState UpdateState => _updateState;

    /// <summary>True on Windows hosts, used by the Razor template to hide the
    /// Service Account card on non-Windows dev boxes.</summary>
    public bool IsWindows => OperatingSystem.IsWindows();

    public Dictionary<string, string> AllSettings { get; set; } = new();

    /// <summary>
    /// Identity the host process is currently running as. On Windows this is the
    /// service Log On account; in dev or on macOS it's the local user. Surfaced
    /// on the Velocity Adapter settings page so operators can confirm the
    /// account that needs to be a Velocity Operator.
    /// </summary>
    public string CurrentServiceAccount { get; set; } = "";

    /// <summary>
    /// Resolved relay URL — either the user-configured override or the
    /// hardcoded default. Used by the Push Relay section so the user always
    /// sees the URL their instance will actually talk to.
    /// </summary>
    public string EffectiveRelayUrl { get; set; } = "";

    /// <summary>
    /// One of "registered", "pending", "rejected", "expired", or "idle".
    /// Drives which Push Relay branch the Razor template renders.
    /// </summary>
    public string RelayState
    {
        get
        {
            if (Get("Relay:Registered") == "true") return "registered";
            return Get("Relay:RequestStatus") switch
            {
                "pending" => "pending",
                "rejected" => "rejected",
                "expired" => "expired",
                _ => "idle",
            };
        }
    }

    public string Get(string key, string defaultValue = "")
    {
        return AllSettings.TryGetValue(key, out var value) ? value : defaultValue;
    }

    public async Task OnGetAsync()
    {
        AllSettings = await _settings.GetAllAsync();
        CurrentServiceAccount = ResolveCurrentServiceAccount();
        EffectiveRelayUrl = await _relayUrlResolver.GetAsync();
    }

    private static string ResolveCurrentServiceAccount()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                if (!string.IsNullOrEmpty(identity.Name))
                    return identity.Name;
            }
            catch
            {
                // Fall through to Environment.UserName.
            }
        }
        var domain = Environment.UserDomainName;
        var user = Environment.UserName;
        return string.IsNullOrEmpty(domain) ? user : $"{domain}\\{user}";
    }

    public async Task<IActionResult> OnPostAsync(Dictionary<string, string> settings, Dictionary<string, string> encryptedSettings)
    {
        foreach (var (key, value) in settings)
        {
            if (!string.IsNullOrEmpty(value))
                await _settings.SetAsync(key, value);
        }

        foreach (var (key, value) in encryptedSettings)
        {
            if (!string.IsNullOrEmpty(value))
                await _settings.SetEncryptedAsync(key, value);
        }

        TempData["Success"] = "Settings saved.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestConnectionAsync()
    {
        var loginUrl = await _settings.GetAsync("WebSocket:LoginUrl");
        var username = await _settings.GetAsync("WebSocket:Username");
        var password = await _settings.GetEncryptedAsync("WebSocket:Password");
        var wsUrl = await _settings.GetAsync("WebSocket:Url");
        var usernameField = await _settings.GetAsync("WebSocket:UsernameField") ?? "UserName";
        var passwordField = await _settings.GetAsync("WebSocket:PasswordField") ?? "Password";
        var tokenField = await _settings.GetAsync("WebSocket:TokenField") ?? "Token";
        var domain = await _settings.GetAsync("WebSocket:Domain");

        if (string.IsNullOrEmpty(loginUrl) || string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password) || string.IsNullOrEmpty(wsUrl))
        {
            TempData["Error"] = "WebSocket settings are incomplete.";
            return RedirectToPage();
        }

        var additionalFields = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(domain))
            additionalFields["Domain"] = domain;

        var token = await _authService.GetTokenAsync(loginUrl, username, password, usernameField, passwordField, tokenField, additionalFields);
        if (string.IsNullOrEmpty(token))
        {
            TempData["Error"] = "Authentication failed. Check login URL and credentials.";
            return RedirectToPage();
        }

        try
        {
            using var ws = new ClientWebSocket();
            ws.Options.SetRequestHeader("Authorization", $"Bearer {token}");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await ws.ConnectAsync(new Uri(wsUrl), cts.Token);
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Test", cts.Token);
            TempData["Success"] = "Connection test successful! Authenticated and connected to WebSocket.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = $"WebSocket connection failed: {ex.Message}";
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestVelocityConnectionAsync()
    {
        var config = await VelocityConnectionResolver.ResolveAsync(_settings);
        if (config == null)
        {
            TempData["Error"] =
                "Velocity connection settings are incomplete. Set SQL Server and Database " +
                "under Settings → Velocity Adapter, or install on a machine with the Velocity client registry.";
            return RedirectToPage();
        }

        try
        {
            var server = new VelocityAdapter.VelocityServer(bEnableLogging: false);
            var connected = new TaskCompletionSource<bool>();
            string? errorMessage = null;

            server.ConnectionSuccess += () => connected.TrySetResult(true);
            server.ConnectionFailure += (string msg) =>
            {
                errorMessage = msg;
                connected.TrySetResult(false);
            };

            VelocityConnectionResolver.ApplyConnect(server, config);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(15, config.ConnectionTimeoutSec)));
            var success = await connected.Task.WaitAsync(cts.Token);

            if (success)
            {
                var version = server.VelocityRelease;
                server.Disconnect();
                TempData["Success"] = $"Connected to Velocity! Server: {config.SqlServer}, Database: {config.Database}" +
                    (string.IsNullOrEmpty(version) ? "" : $", Version: {version}");
            }
            else
            {
                TempData["Error"] = $"Velocity connection failed: {errorMessage ?? "Unknown error"}";
            }
        }
        catch (OperationCanceledException)
        {
            TempData["Error"] = $"Velocity connection timed out after {Math.Max(15, config.ConnectionTimeoutSec)} seconds.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = $"Velocity connection error: {ex.Message}";
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestNotificationAsync()
    {
        var active = await _db.Recipients.Include(r => r.ContactMethods).Where(r => r.IsActive).ToListAsync();
        if (!active.Any())
        {
            TempData["Error"] = "No active recipients to send test notification to.";
            return RedirectToPage();
        }

        var sent = 0;
        foreach (var r in active)
        {
            if (await _notificationSender.SendAsync(r, "Test notification from Hirsch Notify."))
                sent++;
        }

        TempData["Success"] = $"Test notification sent to {sent} of {active.Count} active recipients.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRequestRegistrationTokenAsync()
    {
        var serviceName = await _settings.GetAsync("Service:Name") ?? "";
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            TempData["Error"] = "Set a Service Name above before requesting a relay registration.";
            return RedirectToPage();
        }

        try
        {
            var req = await _relayClient.RequestRegistrationTokenAsync(serviceName, "1.0.0");
            await _settings.SetAsync("Relay:RequestId", req.RequestId);
            await _settings.SetEncryptedAsync("Relay:RequestSecret", req.RequestSecret);
            await _settings.SetAsync("Relay:RequestStatus", "pending");
            await _settings.SetAsync("Relay:RequestedAt", DateTime.UtcNow.ToString("O"));
            await _settings.SetAsync("Relay:RejectionReason", "");
            TempData["Success"] = "Registration request submitted. Waiting for admin approval.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to submit relay registration request");
            TempData["Error"] = $"Could not submit registration request: {ex.Message}";
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCancelRegistrationRequestAsync()
    {
        var requestId = await _settings.GetAsync("Relay:RequestId") ?? "";
        var requestSecret = await _settings.GetEncryptedAsync("Relay:RequestSecret") ?? "";
        if (!string.IsNullOrEmpty(requestId) && !string.IsNullOrEmpty(requestSecret))
        {
            try
            {
                await _relayClient.CancelRegistrationRequestAsync(requestId, requestSecret);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cancel request failed on server; clearing locally anyway");
            }
        }
        await ClearRequestKeysAsync();
        TempData["Success"] = "Registration request cancelled.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDismissRegistrationResultAsync()
    {
        await ClearRequestKeysAsync();
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostUnregisterRelayAsync()
    {
        await _settings.SetAsync("Relay:Registered", "false");
        await _settings.SetAsync("Relay:InstanceId", "");
        await _settings.SetAsync("Relay:InstanceName", "");
        await _settings.SetAsync("Relay:Url", "");
        await ClearRequestKeysAsync();

        TempData["Success"] = "Unregistered from relay.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostInstallUpdateAsync()
    {
        var manifest = _updateState.LatestManifest;
        if (manifest is null || !_updateState.IsUpdateAvailable())
        {
            TempData["Error"] = "No update is currently available.";
            return RedirectToPage();
        }

        try
        {
            var setupPath = await _updateChecker.DownloadAsync(manifest, HttpContext.RequestAborted);
            _updateChecker.InstallAndExit(setupPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to install update {Version}", manifest.Version);
            TempData["Error"] = $"Failed to start installer: {ex.Message}";
            return RedirectToPage();
        }

        TempData["Success"] = $"Installing v{manifest.Version} — this page will reconnect in ~30 seconds.";
        TempData["Restarting"] = true;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSetServiceAccountAsync(string username, string password)
    {
        if (!OperatingSystem.IsWindows())
        {
            TempData["Error"] = "Service account management is only supported on Windows.";
            return RedirectToPage();
        }

        var validation = await _serviceAccountManager.ValidateAsync(username, password, HttpContext.RequestAborted);
        if (!validation.IsValid)
        {
            TempData["Error"] = validation.ErrorMessage ?? "Credential validation failed.";
            return RedirectToPage();
        }

        try
        {
            await _serviceAccountManager.ApplyAsync(username, password, HttpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply new service account {Account}", username);
            TempData["Error"] = $"Failed to update service account: {ex.Message}";
            return RedirectToPage();
        }

        TempData["Success"] = "Service account updated. Restarting — this page will reconnect in ~15 seconds.";
        TempData["Restarting"] = true;
        return RedirectToPage();
    }

    private async Task ClearRequestKeysAsync()
    {
        await _settings.SetAsync("Relay:RequestId", "");
        await _settings.SetAsync("Relay:RequestSecret", "");
        await _settings.SetAsync("Relay:RequestStatus", "");
        await _settings.SetAsync("Relay:RequestedAt", "");
        await _settings.SetAsync("Relay:RejectionReason", "");
    }
}
