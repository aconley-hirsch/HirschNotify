using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace EventAlertService.Services;

public class RelaySender : IRelaySender
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settings;
    private readonly ILogger<RelaySender> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public RelaySender(HttpClient httpClient, ISettingsService settings, ILogger<RelaySender> logger)
    {
        _httpClient = httpClient;
        _settings = settings;
        _logger = logger;
    }

    public async Task<bool> SendAsync(string title, string body, Dictionary<string, string>? data = null, int? recipientId = null)
    {
        try
        {
            var relayUrl = (await _settings.GetAsync("Relay:Url") ?? "").TrimEnd('/');
            var instanceId = await _settings.GetAsync("Relay:InstanceId") ?? "";
            var apiKey = await _settings.GetEncryptedAsync("Relay:ApiKey") ?? "";

            if (string.IsNullOrEmpty(relayUrl) || string.IsNullOrEmpty(instanceId) || string.IsNullOrEmpty(apiKey))
            {
                _logger.LogWarning("Relay not configured, skipping push notification");
                return false;
            }

            var url = $"{relayUrl}/api/v1/instances/{instanceId}/notifications";
            var payload = new
            {
                encrypted = false,
                recipientIds = recipientId.HasValue ? new[] { recipientId.Value } : Array.Empty<int>(),
                title,
                body,
                data = data ?? new Dictionary<string, string>()
            };

            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(payload, JsonOptions),
                    Encoding.UTF8,
                    "application/json"
                )
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var response = await _httpClient.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Push notification sent via relay for instance {InstanceId}", instanceId);
                return true;
            }

            _logger.LogError("Relay notification failed: {Status} - {Body}", response.StatusCode, responseBody);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send push notification via relay");
            return false;
        }
    }
}
