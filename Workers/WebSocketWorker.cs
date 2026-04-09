using System.Net.WebSockets;
using System.Text;
using HirschNotify.Services;

namespace HirschNotify.Workers;

public class WebSocketWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConnectionState _connectionState;
    private readonly IEventProcessor _eventProcessor;
    private readonly ILogger<WebSocketWorker> _logger;

    public WebSocketWorker(IServiceScopeFactory scopeFactory, ConnectionState connectionState, IEventProcessor eventProcessor, ILogger<WebSocketWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _connectionState = connectionState;
        _eventProcessor = eventProcessor;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(2000, stoppingToken);

            // Check if WebSocket mode is enabled
            using (var scope = _scopeFactory.CreateScope())
            {
                var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
                var mode = await settings.GetAsync("EventSource:Mode") ?? "WebSocket";
                if (mode == "VelocityAdapter")
                {
                    _logger.LogInformation("Event source set to VelocityAdapter — WebSocket worker disabled");
                    return;
                }
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ConnectAndListenAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "WebSocket connection error");
                    _connectionState.Status = "Disconnected";
                    _connectionState.ConnectedSince = null;
                    await Task.Delay(5000, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown
        }
    }

    private async Task ConnectAndListenAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        var wsUrl = await settings.GetAsync("WebSocket:Url");
        var loginUrl = await settings.GetAsync("WebSocket:LoginUrl");
        var username = await settings.GetAsync("WebSocket:Username");
        var password = await settings.GetEncryptedAsync("WebSocket:Password");
        var usernameField = await settings.GetAsync("WebSocket:UsernameField") ?? "UserName";
        var passwordField = await settings.GetAsync("WebSocket:PasswordField") ?? "Password";
        var tokenField = await settings.GetAsync("WebSocket:TokenField") ?? "Token";
        var domain = await settings.GetAsync("WebSocket:Domain");

        if (string.IsNullOrEmpty(wsUrl) || string.IsNullOrEmpty(loginUrl) ||
            string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            _logger.LogWarning("WebSocket settings not configured. Waiting...");
            await Task.Delay(30000, stoppingToken);
            return;
        }

        // Build additional login fields
        var additionalFields = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(domain))
            additionalFields["Domain"] = domain;

        // Authenticate
        var authService = scope.ServiceProvider.GetRequiredService<IWebSocketAuthService>();
        var token = await authService.GetTokenAsync(loginUrl, username, password, usernameField, passwordField, tokenField, additionalFields);

        if (string.IsNullOrEmpty(token))
        {
            _logger.LogError("Failed to get authentication token");
            _connectionState.Status = "Auth Failed";
            await Task.Delay(30000, stoppingToken);
            return;
        }

        // Connect WebSocket
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", $"Bearer {token}");

        _connectionState.Status = "Connecting";
        await ws.ConnectAsync(new Uri(wsUrl), stoppingToken);

        _connectionState.Status = "Connected";
        _connectionState.ConnectedSince = DateTime.UtcNow;
        _logger.LogInformation("Connected to WebSocket at {Url}", wsUrl);

        // Listen loop
        var buffer = new byte[4096];
        var messageBuffer = new StringBuilder();

        while (ws.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), stoppingToken);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                _logger.LogInformation("WebSocket closed by server");
                break;
            }

            if (result.MessageType == WebSocketMessageType.Text)
            {
                messageBuffer.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                if (result.EndOfMessage)
                {
                    var message = messageBuffer.ToString();
                    messageBuffer.Clear();
                    _connectionState.IncrementEvents();

                    _logger.LogInformation("WebSocket message received: {Message}", message.Length > 500 ? message[..500] + "..." : message);
                    await _eventProcessor.ProcessEventAsync(message, stoppingToken);
                }
            }
        }

        _connectionState.Status = "Disconnected";
        _connectionState.ConnectedSince = null;
    }

}
