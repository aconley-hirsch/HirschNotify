using HirschNotify.Services;
using HirschNotify.Services.Health;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HirschNotify.Pages.Health;

[Authorize]
public class IndexModel : PageModel
{
    private static readonly WindowsServiceHealthSettings WinDefaults = new();
    private static readonly SdkHealthSettings SdkDefaults = new();

    private readonly ISettingsService _settings;
    private readonly IWindowsServicesInspector _inspector;

    public IndexModel(ISettingsService settings, IWindowsServicesInspector inspector)
    {
        _settings = settings;
        _inspector = inspector;
    }

    public bool IsWindows => _inspector.IsSupported;

    public List<InstalledWindowsService> InstalledServices { get; set; } = new();

    /// <summary>Service short-names that should be rendered with a checked row.</summary>
    public HashSet<string> SelectedNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Newline-joined patterns — wildcards plus any stored entries that
    /// don't match a currently-installed service.</summary>
    public string CustomPatterns { get; set; } = "";

    public string PollIntervalSeconds { get; set; } = "";

    public bool CriticalOnAutomaticStopped { get; set; } = true;

    // SDK health thresholds
    public string QueueWarnThreshold { get; set; } = "";
    public string QueueCriticalThreshold { get; set; } = "";
    public string SqlLatencyWarnMs { get; set; } = "";
    public string SqlLatencyCriticalMs { get; set; } = "";

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
            entries = WinDefaults.MonitoredServices;
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

        var criticalRaw = await _settings.GetAsync("Health:WindowsServices:CriticalOnAutomaticStopped");
        CriticalOnAutomaticStopped = bool.TryParse(criticalRaw, out var critical) ? critical : WinDefaults.CriticalOnAutomaticStopped;

        // SDK thresholds
        QueueWarnThreshold = await _settings.GetAsync("Health:Sdk:QueueWarnThreshold") ?? "";
        QueueCriticalThreshold = await _settings.GetAsync("Health:Sdk:QueueCriticalThreshold") ?? "";
        SqlLatencyWarnMs = await _settings.GetAsync("Health:Sdk:SqlLatencyWarnMs") ?? "";
        SqlLatencyCriticalMs = await _settings.GetAsync("Health:Sdk:SqlLatencyCriticalMs") ?? "";

        if (inheritingDefaults)
            ViewData["InheritingDefaults"] = true;

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(
        List<string>? selected,
        string? customPatterns,
        string? pollIntervalSeconds,
        bool criticalOnAutomaticStopped,
        string? queueWarnThreshold,
        string? queueCriticalThreshold,
        string? sqlLatencyWarnMs,
        string? sqlLatencyCriticalMs)
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

        await _settings.SetAsync("Health:WindowsServices:CriticalOnAutomaticStopped", criticalOnAutomaticStopped.ToString().ToLowerInvariant());

        // SDK thresholds — save as-is; empty clears the override (C# default kicks in).
        await _settings.SetAsync("Health:Sdk:QueueWarnThreshold", queueWarnThreshold?.Trim() ?? "");
        await _settings.SetAsync("Health:Sdk:QueueCriticalThreshold", queueCriticalThreshold?.Trim() ?? "");
        await _settings.SetAsync("Health:Sdk:SqlLatencyWarnMs", sqlLatencyWarnMs?.Trim() ?? "");
        await _settings.SetAsync("Health:Sdk:SqlLatencyCriticalMs", sqlLatencyCriticalMs?.Trim() ?? "");

        TempData["Success"] = "Health monitoring settings saved. Changes take effect on the next poll cycle.";
        return RedirectToPage();
    }

    private static bool IsWildcard(string entry) =>
        entry.Contains('*') || entry.Contains('?');
}
