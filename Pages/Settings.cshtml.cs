using System.Net.WebSockets;
using System.Runtime.InteropServices;
using EventAlertService.Data;
using EventAlertService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Win32;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace EventAlertService.Pages;

[Authorize]
public class SettingsModel : PageModel
{
    private readonly ISettingsService _settings;
    private readonly INotificationSender _notificationSender;
    private readonly IWebSocketAuthService _authService;
    private readonly IRelayClient _relayClient;
    private readonly AppDbContext _db;

    public SettingsModel(ISettingsService settings, INotificationSender notificationSender, IWebSocketAuthService authService, IRelayClient relayClient, AppDbContext db)
    {
        _settings = settings;
        _notificationSender = notificationSender;
        _authService = authService;
        _relayClient = relayClient;
        _db = db;
    }

    public Dictionary<string, string> AllSettings { get; set; } = new();

    public string Get(string key, string defaultValue = "")
    {
        return AllSettings.TryGetValue(key, out var value) ? value : defaultValue;
    }

    public async Task OnGetAsync()
    {
        AllSettings = await _settings.GetAllAsync();
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
        // Read from registry first, then allow settings overrides
        string? sqlServer = null, database = null, appRole = null;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Wow6432Node\Hirsch Electronics\Velocity\Client");
                if (key != null)
                {
                    sqlServer = key.GetValue("SQL Server") as string;
                    database = key.GetValue("Database") as string;
                    appRole = key.GetValue("ApplicationRole") as string;
                }
            }
            catch { }
        }

        var sqlServerOverride = await _settings.GetAsync("Velocity:SqlServer");
        var databaseOverride = await _settings.GetAsync("Velocity:Database");
        if (!string.IsNullOrEmpty(sqlServerOverride)) sqlServer = sqlServerOverride;
        if (!string.IsNullOrEmpty(databaseOverride)) database = databaseOverride;

        if (string.IsNullOrEmpty(sqlServer) || string.IsNullOrEmpty(database))
        {
            TempData["Error"] = "Velocity registry settings not found and no manual override configured.";
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

            if (!string.IsNullOrEmpty(appRole))
                server.ConnectDecrypt(sqlServer, database, appRole);
            else
                server.Connect(sqlServer, database);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var success = await connected.Task.WaitAsync(cts.Token);

            if (success)
            {
                var version = server.VelocityRelease;
                server.Disconnect();
                TempData["Success"] = $"Connected to Velocity! Server: {sqlServer}, Database: {database}" +
                    (string.IsNullOrEmpty(version) ? "" : $", Version: {version}");
            }
            else
            {
                TempData["Error"] = $"Velocity connection failed: {errorMessage ?? "Unknown error"}";
            }
        }
        catch (OperationCanceledException)
        {
            TempData["Error"] = "Velocity connection timed out after 15 seconds.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = $"Velocity connection error: {ex.Message}";
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestNotificationAsync()
    {
        var active = await _db.Recipients.Where(r => r.IsActive).ToListAsync();
        if (!active.Any())
        {
            TempData["Error"] = "No active recipients to send test notification to.";
            return RedirectToPage();
        }

        var sent = 0;
        foreach (var r in active)
        {
            if (await _notificationSender.SendAsync(r, "Test notification from Event Alert Service."))
                sent++;
        }

        TempData["Success"] = $"Test notification sent to {sent} of {active.Count} active recipients.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRegisterRelayAsync(string relayUrl, string registrationToken)
    {
        var serviceName = await _settings.GetAsync("Service:Name") ?? "";
        if (string.IsNullOrEmpty(relayUrl) || string.IsNullOrEmpty(serviceName) || string.IsNullOrEmpty(registrationToken))
        {
            TempData["Error"] = string.IsNullOrEmpty(serviceName)
                ? "Please set a Service Name in the settings above before registering."
                : "All fields are required for relay registration.";
            return RedirectToPage();
        }

        try
        {
            var result = await _relayClient.RegisterAsync(relayUrl, serviceName, "1.0.0", registrationToken);

            await _settings.SetAsync("Relay:Url", relayUrl);
            await _settings.SetAsync("Relay:InstanceId", result.InstanceId);
            await _settings.SetEncryptedAsync("Relay:ApiKey", result.ApiKey);
            await _settings.SetAsync("Relay:Registered", "true");

            TempData["Success"] = $"Registered with relay! Instance ID: {result.InstanceId}";
        }
        catch (Exception ex)
        {
            TempData["Error"] = $"Relay registration failed: {ex.Message}";
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostUnregisterRelayAsync()
    {
        await _settings.SetAsync("Relay:Registered", "false");
        await _settings.SetAsync("Relay:InstanceId", "");
        await _settings.SetAsync("Relay:InstanceName", "");
        await _settings.SetAsync("Relay:Url", "");

        TempData["Success"] = "Unregistered from relay.";
        return RedirectToPage();
    }
}
