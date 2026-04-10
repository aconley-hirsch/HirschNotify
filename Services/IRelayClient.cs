namespace HirschNotify.Services;

public record RelayRegistrationResult(string InstanceId, string ApiKey);
public record RelayPairingCode(string Code, DateTime ExpiresAt);
public record RelayDevice(string DeviceId, string? Label, string Platform, int? RecipientId, DateTime PairedAt, DateTime LastSeen, bool Stale);

/// <summary>
/// Result of POST /api/v1/registration-requests. The plaintext request
/// secret is shown exactly once and must be persisted (encrypted) by the
/// caller — the relay only stores its hash.
/// </summary>
public record RelayRegistrationRequest(string RequestId, string RequestSecret);

/// <summary>
/// Result of GET /api/v1/registration-requests/:id. <see cref="RegistrationToken"/>
/// is populated only on the one-shot delivery transition (status=delivered);
/// every subsequent poll returns delivered with no token.
/// </summary>
public record RelayRegistrationStatus(
    string Status,
    string? RegistrationToken,
    string? RejectionReason);

public interface IRelayClient
{
    Task<RelayRegistrationResult> RegisterAsync(string relayUrl, string name, string version, string registrationToken);
    Task<RelayPairingCode> CreatePairingCodeAsync(string? label, int? recipientId = null);
    Task<List<RelayDevice>> GetDevicesAsync();
    Task RevokeDeviceAsync(string deviceId);
    Task HeartbeatAsync(string name, string status, long eventsToday, long alertsToday);

    // Self-service registration request flow.
    Task<RelayRegistrationRequest> RequestRegistrationTokenAsync(string name, string version);
    Task<RelayRegistrationStatus> PollRegistrationRequestAsync(string requestId, string requestSecret);
    Task CancelRegistrationRequestAsync(string requestId, string requestSecret);
}
