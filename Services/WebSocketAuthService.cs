using System.Text;
using System.Text.Json;

namespace HirschNotify.Services;

public class WebSocketAuthService : IWebSocketAuthService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<WebSocketAuthService> _logger;

    public WebSocketAuthService(HttpClient httpClient, ILogger<WebSocketAuthService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<string?> GetTokenAsync(string loginUrl, string username, string password,
        string usernameField, string passwordField, string tokenField,
        Dictionary<string, string>? additionalFields = null)
    {
        try
        {
            var payload = new Dictionary<string, string>
            {
                [usernameField] = username,
                [passwordField] = password
            };

            if (additionalFields != null)
            {
                foreach (var (key, value) in additionalFields)
                    payload[key] = value;
            }

            var json = JsonSerializer.Serialize(payload);
            _logger.LogInformation("Login request to {LoginUrl}: {RequestBody}", loginUrl, json);

            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(loginUrl, content);
            var responseBody = await response.Content.ReadAsStringAsync();
            _logger.LogInformation("Login response ({StatusCode}): {ResponseBody}", (int)response.StatusCode, responseBody);

            response.EnsureSuccessStatusCode();

            var doc = JsonDocument.Parse(responseBody);

            if (doc.RootElement.TryGetProperty(tokenField, out var tokenElement))
            {
                var token = tokenElement.GetString();
                _logger.LogInformation("Successfully authenticated with WebSocket endpoint");
                return token;
            }

            _logger.LogError("Token field '{TokenField}' not found in login response", tokenField);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to authenticate with WebSocket endpoint at {LoginUrl}", loginUrl);
            return null;
        }
    }
}
