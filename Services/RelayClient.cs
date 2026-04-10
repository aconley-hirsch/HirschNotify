using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace HirschNotify.Services;

public class RelayClient : IRelayClient
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settings;
    private readonly RelayUrlResolver _urlResolver;
    private readonly ILogger<RelayClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public RelayClient(HttpClient httpClient, ISettingsService settings, RelayUrlResolver urlResolver, ILogger<RelayClient> logger)
    {
        _httpClient = httpClient;
        _settings = settings;
        _urlResolver = urlResolver;
        _logger = logger;
    }

    public async Task<RelayRegistrationResult> RegisterAsync(string relayUrl, string name, string version, string registrationToken)
    {
        var url = $"{relayUrl.TrimEnd('/')}/api/v1/instances/register";
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent(new { name, version })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", registrationToken);

        var response = await _httpClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Relay registration failed: {Status} - {Body}", response.StatusCode, body);
            throw new Exception($"Registration failed: {ParseErrorMessage(body)}");
        }

        var result = JsonSerializer.Deserialize<JsonElement>(body);
        return new RelayRegistrationResult(
            result.GetProperty("instanceId").GetString()!,
            result.GetProperty("apiKey").GetString()!
        );
    }

    public async Task<RelayPairingCode> CreatePairingCodeAsync(string? label, int? recipientId = null)
    {
        var (url, apiKey) = await GetRelayConfig();
        var instanceId = await _settings.GetAsync("Relay:InstanceId") ?? "";

        object payload = recipientId.HasValue
            ? new { label = label ?? "", recipientId = recipientId.Value }
            : new { label = label ?? "" };

        var request = new HttpRequestMessage(HttpMethod.Post, $"{url}/api/v1/instances/{instanceId}/pairing-codes")
        {
            Content = JsonContent(payload)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var response = await _httpClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Failed to create pairing code: {Status} - {Body}", response.StatusCode, body);
            throw new Exception($"Failed to create pairing code: {ParseErrorMessage(body)}");
        }

        var result = JsonSerializer.Deserialize<JsonElement>(body);
        return new RelayPairingCode(
            result.GetProperty("code").GetString()!,
            result.GetProperty("expiresAt").GetDateTime()
        );
    }

    public async Task<List<RelayDevice>> GetDevicesAsync()
    {
        var (url, apiKey) = await GetRelayConfig();
        var instanceId = await _settings.GetAsync("Relay:InstanceId") ?? "";

        var request = new HttpRequestMessage(HttpMethod.Get, $"{url}/api/v1/instances/{instanceId}/devices");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var response = await _httpClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Failed to get devices: {Status} - {Body}", response.StatusCode, body);
            return new List<RelayDevice>();
        }

        var result = JsonSerializer.Deserialize<JsonElement>(body);
        var devices = new List<RelayDevice>();

        foreach (var d in result.GetProperty("devices").EnumerateArray())
        {
            devices.Add(new RelayDevice(
                d.GetProperty("deviceId").GetString()!,
                d.TryGetProperty("label", out var label) ? label.GetString() : null,
                d.GetProperty("platform").GetString()!,
                d.TryGetProperty("recipientId", out var rid) && rid.ValueKind == JsonValueKind.Number ? rid.GetInt32() : null,
                d.GetProperty("pairedAt").GetDateTime(),
                d.GetProperty("lastSeen").GetDateTime(),
                d.TryGetProperty("stale", out var stale) && stale.GetBoolean()
            ));
        }

        return devices;
    }

    public async Task RevokeDeviceAsync(string deviceId)
    {
        var (url, apiKey) = await GetRelayConfig();
        var instanceId = await _settings.GetAsync("Relay:InstanceId") ?? "";

        var request = new HttpRequestMessage(HttpMethod.Delete, $"{url}/api/v1/instances/{instanceId}/devices/{deviceId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            _logger.LogError("Failed to revoke device: {Status} - {Body}", response.StatusCode, body);
            throw new Exception($"Failed to revoke device: {ParseErrorMessage(body)}");
        }
    }

    public async Task HeartbeatAsync(string name, string status, long eventsToday, long alertsToday)
    {
        var (url, apiKey) = await GetRelayConfig();
        var instanceId = await _settings.GetAsync("Relay:InstanceId") ?? "";

        var request = new HttpRequestMessage(HttpMethod.Post, $"{url}/api/v1/instances/{instanceId}/heartbeat")
        {
            Content = JsonContent(new { name, status, eventsToday, alertsToday })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Heartbeat failed: {Status} - {Body}", response.StatusCode, body);
        }
    }

    private async Task<(string url, string apiKey)> GetRelayConfig()
    {
        var url = await _urlResolver.GetAsync();
        var apiKey = await _settings.GetEncryptedAsync("Relay:ApiKey") ?? "";

        if (string.IsNullOrEmpty(apiKey))
            throw new InvalidOperationException("Relay is not configured. Register first.");

        return (url, apiKey);
    }

    public async Task<RelayRegistrationRequest> RequestRegistrationTokenAsync(string name, string version)
    {
        var url = await _urlResolver.GetAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, $"{url}/api/v1/registration-requests")
        {
            Content = JsonContent(new { name, version })
        };

        var response = await _httpClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Failed to submit registration request: {Status} - {Body}", response.StatusCode, body);
            throw new Exception($"Failed to submit registration request: {ParseErrorMessage(body)}");
        }

        var result = JsonSerializer.Deserialize<JsonElement>(body);
        return new RelayRegistrationRequest(
            result.GetProperty("requestId").GetString()!,
            result.GetProperty("requestSecret").GetString()!
        );
    }

    public async Task<RelayRegistrationStatus> PollRegistrationRequestAsync(string requestId, string requestSecret)
    {
        var url = await _urlResolver.GetAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, $"{url}/api/v1/registration-requests/{requestId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", requestSecret);

        var response = await _httpClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        // 401 / 404 from the relay both surface as ErrInvalidRequestSecret —
        // the server has either forgotten the request or the secret is bad.
        // Either way the worker should treat it as terminal and stop polling.
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized
            || response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogWarning("Registration request {RequestId} no longer recognised by relay", requestId);
            return new RelayRegistrationStatus("cancelled", null, null);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Failed to poll registration request: {Status} - {Body}", response.StatusCode, body);
            throw new Exception($"Failed to poll registration request: {ParseErrorMessage(body)}");
        }

        var result = JsonSerializer.Deserialize<JsonElement>(body);
        var status = result.GetProperty("status").GetString() ?? "";
        string? token = null;
        string? reason = null;
        if (result.TryGetProperty("registrationToken", out var t) && t.ValueKind == JsonValueKind.String)
            token = t.GetString();
        if (result.TryGetProperty("rejectionReason", out var r) && r.ValueKind == JsonValueKind.String)
            reason = r.GetString();

        return new RelayRegistrationStatus(status, token, reason);
    }

    public async Task CancelRegistrationRequestAsync(string requestId, string requestSecret)
    {
        var url = await _urlResolver.GetAsync();
        var request = new HttpRequestMessage(HttpMethod.Delete, $"{url}/api/v1/registration-requests/{requestId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", requestSecret);

        var response = await _httpClient.SendAsync(request);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return; // already gone, idempotent

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Cancel registration request returned {Status}: {Body}", response.StatusCode, body);
        }
    }

    private static StringContent JsonContent(object payload)
    {
        return new StringContent(
            JsonSerializer.Serialize(payload, JsonOptions),
            Encoding.UTF8,
            "application/json"
        );
    }

    private static string ParseErrorMessage(string body)
    {
        try
        {
            var err = JsonSerializer.Deserialize<JsonElement>(body);
            return err.TryGetProperty("message", out var msg) ? msg.GetString()! : body;
        }
        catch
        {
            return body;
        }
    }
}
