namespace HirschNotify.Services;

/// <summary>
/// Single source of truth for "what's the relay base URL?". Returns the
/// hardcoded default <see cref="Default"/> unless an operator has explicitly
/// overridden it via the <c>Relay:Url</c> setting (used by self-hosters).
/// </summary>
/// <remarks>
/// Centralising this avoids the previous pattern where every caller inlined
/// <c>_settings.GetAsync("Relay:Url")</c>, which made it impossible to ship
/// a default URL without touching half a dozen files.
/// </remarks>
public sealed class RelayUrlResolver
{
    /// <summary>
    /// Default relay URL baked into the binary. Operators can override on
    /// the Settings page if they're self-hosting their own relay; otherwise
    /// every fresh install talks to the public relay.
    /// </summary>
    public const string Default = "https://relay.arick.dev";

    private readonly ISettingsService _settings;

    public RelayUrlResolver(ISettingsService settings) => _settings = settings;

    public async Task<string> GetAsync()
    {
        var configured = (await _settings.GetAsync("Relay:Url") ?? "").Trim();
        return string.IsNullOrEmpty(configured)
            ? Default
            : configured.TrimEnd('/');
    }
}
