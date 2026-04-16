using HirschNotify.Services;
using HirschNotify.Services.Health;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace HirschNotify.Pages.Health;

[Authorize]
public class IndexModel : PageModel
{
    private readonly ISettingsService _settings;
    private readonly IWindowsServicesInspector _inspector;
    private readonly IOptionsMonitor<HirschNotify.Services.Health.HealthSettings> _options;

    public IndexModel(
        ISettingsService settings,
        IWindowsServicesInspector inspector,
        IOptionsMonitor<HirschNotify.Services.Health.HealthSettings> options)
    {
        _settings = settings;
        _inspector = inspector;
        _options = options;
    }

    public bool IsWindows => _inspector.IsSupported;

    public List<InstalledWindowsService> InstalledServices { get; set; } = new();

    /// <summary>Service short-names that should be rendered with a checked row.</summary>
    public HashSet<string> SelectedNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Newline-joined patterns — wildcards plus any stored entries that
    /// don't match a currently-installed service.</summary>
    public string CustomPatterns { get; set; } = "";

    public string PollIntervalSeconds { get; set; } = "";

    public bool EmitSnapshots { get; set; }

    public bool CriticalOnAutomaticStopped { get; set; } = true;

    public int EffectiveDefaultPollInterval
    {
        get
        {
            var ws = _options.CurrentValue.WindowsServices;
            return ws.PollIntervalSeconds ?? _options.CurrentValue.PollIntervalSeconds;
        }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        var mode = await _settings.GetAsync("EventSource:Mode") ?? "WebSocket";
        if (mode != "VelocityAdapter")
        {
            TempData["Error"] = "SRE Health Monitoring is only active when Event Source is set to Velocity Adapter.";
            return RedirectToPage("/Settings");
        }

        InstalledServices = _inspector.List().ToList();

        var stored = await _settings.GetAsync("Health:WindowsServices:MonitoredServices");
        IEnumerable<string> entries;
        bool inheritingDefaults;
        if (stored is null)
        {
            // No override saved yet — pre-populate from appsettings.json defaults
            // so first-time visitors see the built-in list, not an empty picker.
            entries = _options.CurrentValue.WindowsServices.MonitoredServices;
            inheritingDefaults = true;
        }
        else
        {
            entries = stored.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(s => s.Trim())
                            .Where(s => s.Length > 0);
            inheritingDefaults = false;
        }

        var installedIndex = new HashSet<string>(
            InstalledServices.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);

        var patterns = new List<string>();
        foreach (var entry in entries)
        {
            if (IsWildcard(entry))
                patterns.Add(entry);
            else if (installedIndex.Contains(entry))
                SelectedNames.Add(entry);
            else
                patterns.Add(entry);
        }

        CustomPatterns = string.Join('\n', patterns);

        PollIntervalSeconds = await _settings.GetAsync("Health:WindowsServices:PollIntervalSeconds") ?? "";

        var emitRaw = await _settings.GetAsync("Health:WindowsServices:EmitSnapshots");
        EmitSnapshots = bool.TryParse(emitRaw, out var emit)
            ? emit
            : _options.CurrentValue.WindowsServices.EmitSnapshots;

        var criticalRaw = await _settings.GetAsync("Health:WindowsServices:CriticalOnAutomaticStopped");
        CriticalOnAutomaticStopped = bool.TryParse(criticalRaw, out var critical)
            ? critical
            : _options.CurrentValue.WindowsServices.CriticalOnAutomaticStopped;

        if (inheritingDefaults)
            ViewData["InheritingDefaults"] = true;

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(
        List<string>? selected,
        string? customPatterns,
        string? pollIntervalSeconds,
        bool emitSnapshots,
        bool criticalOnAutomaticStopped)
    {
        var concrete = (selected ?? new List<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var patternLines = (customPatterns ?? "")
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

        // Empty string is a valid explicit value here — means "monitor nothing"
        // — and differs from a missing key (which means "inherit appsettings").
        var merged = string.Join('\n', concrete.Concat(patternLines));
        await _settings.SetAsync("Health:WindowsServices:MonitoredServices", merged);

        if (int.TryParse(pollIntervalSeconds, out var interval) && interval > 0)
            await _settings.SetAsync("Health:WindowsServices:PollIntervalSeconds", interval.ToString());
        else
            await _settings.SetAsync("Health:WindowsServices:PollIntervalSeconds", "");

        await _settings.SetAsync("Health:WindowsServices:EmitSnapshots", emitSnapshots.ToString().ToLowerInvariant());
        await _settings.SetAsync("Health:WindowsServices:CriticalOnAutomaticStopped", criticalOnAutomaticStopped.ToString().ToLowerInvariant());

        TempData["Success"] = "Health monitoring settings saved. Changes take effect on the next poll cycle.";
        return RedirectToPage();
    }

    private static bool IsWildcard(string entry) =>
        entry.Contains('*') || entry.Contains('?');
}
