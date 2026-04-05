namespace EventAlertService.Services;

public record RelayRegistrationResult(string InstanceId, string ApiKey);
public record RelayPairingCode(string Code, DateTime ExpiresAt);
public record RelayDevice(string DeviceId, string? Label, string Platform, int? RecipientId, DateTime PairedAt, DateTime LastSeen, bool Stale);

public interface IRelayClient
{
    Task<RelayRegistrationResult> RegisterAsync(string relayUrl, string name, string version, string registrationToken);
    Task<RelayPairingCode> CreatePairingCodeAsync(string? label, int? recipientId = null);
    Task<List<RelayDevice>> GetDevicesAsync();
    Task RevokeDeviceAsync(string deviceId);
    Task HeartbeatAsync(string name, string status, long eventsToday, long alertsToday);
}
