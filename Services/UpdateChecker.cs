using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

namespace HirschNotify.Services;

/// <summary>
/// Calls the HirschRelay-hosted update proxy with the same bearer-token
/// pattern <see cref="RelayClient"/> uses for the other instance endpoints.
/// </summary>
public sealed class UpdateChecker : IUpdateChecker
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settings;
    private readonly RelayUrlResolver _urlResolver;
    private readonly ILogger<UpdateChecker> _logger;

    public UpdateChecker(
        HttpClient httpClient,
        ISettingsService settings,
        RelayUrlResolver urlResolver,
        ILogger<UpdateChecker> logger)
    {
        _httpClient = httpClient;
        _settings = settings;
        _urlResolver = urlResolver;
        _logger = logger;
    }

    public async Task<UpdateManifest?> CheckAsync(CancellationToken ct)
    {
        var (relayBase, instanceId, apiKey) = await GetRelayConfigAsync();
        if (relayBase is null || instanceId is null || apiKey is null)
        {
            _logger.LogDebug("Update check skipped: relay not yet registered");
            return null;
        }

        var url = $"{relayBase}/api/v1/instances/{instanceId}/updates/latest";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await _httpClient.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Update check returned {Status}: {Body}", response.StatusCode, body);
            return null;
        }

        var json = JsonSerializer.Deserialize<JsonElement>(body);
        return new UpdateManifest(
            Version: GetString(json, "version") ?? "0.0.0",
            Tag: GetString(json, "tag") ?? "",
            ReleaseNotes: GetString(json, "releaseNotes") ?? "",
            DownloadPath: GetString(json, "downloadPath") ?? "",
            AssetName: GetString(json, "assetName") ?? "",
            AssetSize: json.TryGetProperty("assetSize", out var size) && size.ValueKind == JsonValueKind.Number ? size.GetInt64() : 0);
    }

    public async Task<string> DownloadAsync(UpdateManifest manifest, CancellationToken ct)
    {
        var (relayBase, _, apiKey) = await GetRelayConfigAsync();
        if (relayBase is null || apiKey is null)
            throw new InvalidOperationException("Relay is not configured — cannot download update.");

        var url = $"{relayBase}{manifest.DownloadPath}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var tempPath = Path.Combine(Path.GetTempPath(), manifest.AssetName);
        await using (var file = File.Create(tempPath))
        await using (var upstream = await response.Content.ReadAsStreamAsync(ct))
        {
            await upstream.CopyToAsync(file, ct);
        }

        _logger.LogInformation("Downloaded update {Version} to {Path}", manifest.Version, tempPath);
        return tempPath;
    }

    public void InstallAndExit(string setupExePath)
    {
        // Spawn a detached cmd.exe that waits a beat (so this HTTP response
        // has time to flush), then runs Setup.exe silently. Inno Setup's
        // /SILENT mode stops the service, replaces files, restarts. Using
        // UseShellExecute=true + no wait makes cmd.exe outlive this process,
        // which is essential because Inno will kill us mid-install.
        var script = $"/c timeout /t 3 /nobreak > nul & \"{setupExePath}\" /SILENT /SUPPRESSMSGBOXES /NORESTART";
        var psi = new ProcessStartInfo("cmd.exe", script)
        {
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        Process.Start(psi);
        _logger.LogInformation("Detached silent installer launched: {Path}", setupExePath);
    }

    private async Task<(string? RelayBase, string? InstanceId, string? ApiKey)> GetRelayConfigAsync()
    {
        var registered = await _settings.GetAsync("Relay:Registered");
        if (registered != "true") return (null, null, null);

        var instanceId = await _settings.GetAsync("Relay:InstanceId");
        if (string.IsNullOrEmpty(instanceId)) return (null, null, null);

        var apiKey = await _settings.GetEncryptedAsync("Relay:ApiKey");
        if (string.IsNullOrEmpty(apiKey)) return (null, null, null);

        var relayBase = (await _urlResolver.GetAsync()).TrimEnd('/');
        return (relayBase, instanceId, apiKey);
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
}
