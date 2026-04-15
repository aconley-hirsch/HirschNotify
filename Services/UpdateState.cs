namespace HirschNotify.Services;

/// <summary>
/// Singleton cache of the latest update check result. Written by
/// <see cref="Workers.UpdateCheckerWorker"/> on a 6-hour poll, read by
/// the <c>_Layout</c> banner and the Settings &gt; About section.
/// </summary>
/// <remarks>
/// Field access is synchronous — updates are infrequent enough (every
/// ~6 hours) that a lock isn't worth the complexity. The worker assigns
/// the whole record in one go.
/// </remarks>
public sealed class UpdateState
{
    private readonly Lock _gate = new();
    private UpdateManifest? _latestManifest;
    private DateTime? _lastCheckedAt;
    private string? _lastCheckError;

    public UpdateManifest? LatestManifest
    {
        get { lock (_gate) return _latestManifest; }
    }

    public DateTime? LastCheckedAt
    {
        get { lock (_gate) return _lastCheckedAt; }
    }

    public string? LastCheckError
    {
        get { lock (_gate) return _lastCheckError; }
    }

    /// <summary>Current executing assembly version, formatted as Major.Minor.Build.</summary>
    public static string CurrentVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "0.0.0";

    public bool IsUpdateAvailable()
    {
        var manifest = LatestManifest;
        if (manifest is null) return false;
        if (!Version.TryParse(manifest.Version, out var latest)) return false;
        if (!Version.TryParse(CurrentVersion, out var current)) return false;
        return latest > current;
    }

    internal void SetSuccess(UpdateManifest manifest)
    {
        lock (_gate)
        {
            _latestManifest = manifest;
            _lastCheckedAt = DateTime.UtcNow;
            _lastCheckError = null;
        }
    }

    internal void SetError(string message)
    {
        lock (_gate)
        {
            _lastCheckedAt = DateTime.UtcNow;
            _lastCheckError = message;
        }
    }
}
