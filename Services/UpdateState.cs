using System.Reflection;

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

    /// <summary>
    /// Current running version. Reads <see cref="AssemblyInformationalVersionAttribute"/>
    /// which MinVer sets to the exact git tag value (e.g. <c>1.0.9</c>
    /// or <c>1.0.10-beta.1</c>). Falls back to <c>FileVersion</c> then to
    /// assembly <see cref="AssemblyName.Version"/>.
    /// </summary>
    /// <remarks>
    /// MinVer deliberately pins <c>AssemblyVersion</c> to major-only
    /// (<c>1.0.0.0</c>) to preserve binary compatibility across patch
    /// bumps, so <c>Assembly.GetName().Version</c> is the wrong source
    /// to read from — it always returns <c>1.0.0.0</c>. The
    /// <c>AssemblyInformationalVersion</c> attribute carries the real
    /// string.
    /// </remarks>
    public static string CurrentVersion
    {
        get
        {
            var assembly = Assembly.GetExecutingAssembly();
            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            if (!string.IsNullOrEmpty(informational))
            {
                // Strip any "+commit-sha" build metadata and "-pre" suffix
                // so the display and the version compare both see a clean
                // Major.Minor.Patch.
                var plus = informational.IndexOf('+');
                if (plus >= 0) informational = informational[..plus];
                return informational;
            }

            var fileVersion = assembly
                .GetCustomAttribute<AssemblyFileVersionAttribute>()
                ?.Version;
            if (!string.IsNullOrEmpty(fileVersion))
            {
                return fileVersion;
            }

            return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }
    }

    public bool IsUpdateAvailable()
    {
        var manifest = LatestManifest;
        if (manifest is null) return false;
        if (!Version.TryParse(StripPreRelease(manifest.Version), out var latest)) return false;
        if (!Version.TryParse(StripPreRelease(CurrentVersion), out var current)) return false;
        return latest > current;
    }

    private static string StripPreRelease(string version)
    {
        var dash = version.IndexOf('-');
        return dash >= 0 ? version[..dash] : version;
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
