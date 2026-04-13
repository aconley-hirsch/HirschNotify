using HirschNotify.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HirschNotify.Pages;

[Authorize]
public class IntegrationsModel : PageModel
{
    private readonly IEnumerable<IContactMethodSender> _senders;
    private readonly ISettingsService _settings;

    public IntegrationsModel(IEnumerable<IContactMethodSender> senders, ISettingsService settings)
    {
        _senders = senders;
        _settings = settings;
    }

    public List<IntegrationViewModel> Integrations { get; set; } = new();

    public async Task OnGetAsync()
    {
        await LoadIntegrationsAsync();
    }

    public async Task<IActionResult> OnPostToggleAsync(string type)
    {
        var current = await _settings.GetAsync($"ContactMethod:{type}:Enabled");
        var newValue = current == "true" ? "false" : "true";
        await _settings.SetAsync($"ContactMethod:{type}:Enabled", newValue);
        TempData["Success"] = $"{type} integration {(newValue == "true" ? "enabled" : "disabled")}.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSaveConfigAsync(string type, Dictionary<string, string> config, Dictionary<string, string> secretConfig)
    {
        foreach (var (key, value) in config)
        {
            if (!string.IsNullOrEmpty(value))
                await _settings.SetAsync($"ContactMethod:{type}:{key}", value);
        }

        foreach (var (key, value) in secretConfig)
        {
            if (!string.IsNullOrEmpty(value))
                await _settings.SetEncryptedAsync($"ContactMethod:{type}:{key}", value);
        }

        TempData["Success"] = "Configuration saved.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestAsync(string type, string? testAddress)
    {
        var sender = _senders.FirstOrDefault(s => s.Type == type);
        if (sender == null)
        {
            TempData["Error"] = "Unknown integration type.";
            return RedirectToPage();
        }

        if (string.IsNullOrWhiteSpace(testAddress))
        {
            TempData["Error"] = "Enter a test address.";
            return RedirectToPage();
        }

        // Build a temporary contact method for the test
        var configJson = type switch
        {
            "email" => System.Text.Json.JsonSerializer.Serialize(new { address = testAddress }),
            _ => System.Text.Json.JsonSerializer.Serialize(new { address = testAddress })
        };

        var testMethod = new Models.ContactMethod
        {
            Type = type,
            Label = "Test",
            Configuration = configJson,
            IsActive = true
        };

        var sent = await sender.SendAsync(testMethod, "Test", "Test notification from HirschNotify.");
        TempData[sent ? "Success" : "Error"] = sent
            ? $"Test sent to {testAddress}."
            : "Test failed. Check configuration and logs.";
        return RedirectToPage();
    }

    private async Task LoadIntegrationsAsync()
    {
        var allSettings = await _settings.GetAllAsync();

        foreach (var sender in _senders)
        {
            var vm = new IntegrationViewModel
            {
                Type = sender.Type,
                DisplayName = sender.DisplayName,
                Description = sender.Description,
                IconSvg = sender.IconSvg,
                Fields = sender.ConfigurationFields,
                IsEnabled = allSettings.GetValueOrDefault($"ContactMethod:{sender.Type}:Enabled") == "true"
            };

            foreach (var field in sender.ConfigurationFields)
            {
                var settingsKey = $"ContactMethod:{sender.Type}:{field.Key}";
                if (!field.IsSecret)
                {
                    vm.CurrentValues[field.Key] = allSettings.GetValueOrDefault(settingsKey, "");
                }
                else
                {
                    vm.HasSecretValue[field.Key] = !string.IsNullOrEmpty(allSettings.GetValueOrDefault(settingsKey));
                }
            }

            Integrations.Add(vm);
        }
    }

    public class IntegrationViewModel
    {
        public string Type { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Description { get; set; } = "";
        public string IconSvg { get; set; } = "";
        public ContactMethodField[] Fields { get; set; } = [];
        public bool IsEnabled { get; set; }
        public Dictionary<string, string> CurrentValues { get; set; } = new();
        public Dictionary<string, bool> HasSecretValue { get; set; } = new();
    }
}
