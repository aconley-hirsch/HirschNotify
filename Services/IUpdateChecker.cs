namespace HirschNotify.Services;

/// <summary>
/// Surface for the HirschRelay-proxied update feed. The client polls
/// <see cref="CheckAsync"/> on a 6-hour interval, pulls down new Setup.exe
/// via <see cref="DownloadAsync"/>, and detaches into Inno Setup's silent
/// reinstall via <see cref="InstallAndExit"/>.
/// </summary>
public interface IUpdateChecker
{
    /// <summary>
    /// Fetch the latest release manifest from HirschRelay. Returns null if
    /// the instance isn't registered with a relay yet, or if the relay
    /// returns an error that should be treated as transient (rate limit,
    /// network failure, 5xx).
    /// </summary>
    Task<UpdateManifest?> CheckAsync(CancellationToken ct);

    /// <summary>
    /// Download the Setup.exe for the supplied manifest into
    /// <c>%TEMP%</c>. Returns the absolute path to the downloaded file.
    /// </summary>
    Task<string> DownloadAsync(UpdateManifest manifest, CancellationToken ct);

    /// <summary>
    /// Spawn a detached <c>cmd.exe</c> that invokes the Setup.exe in silent
    /// mode after a short delay. The current process continues running
    /// until the new Setup.exe stops the service as part of its upgrade
    /// dance.
    /// </summary>
    void InstallAndExit(string setupExePath);
}

/// <summary>
/// Shape of the JSON returned by
/// <c>GET /api/v1/instances/:id/updates/latest</c> on HirschRelay.
/// </summary>
public sealed record UpdateManifest(
    string Version,
    string Tag,
    string ReleaseNotes,
    string DownloadPath,
    string AssetName,
    long AssetSize);
