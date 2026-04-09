using HirschNotify.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HirschNotify.Pages;

[Authorize]
public class PairingModel : PageModel
{
    private readonly IRelayClient _relayClient;
    private readonly IRelaySender _relaySender;
    private readonly ISettingsService _settings;

    public PairingModel(IRelayClient relayClient, IRelaySender relaySender, ISettingsService settings)
    {
        _relayClient = relayClient;
        _relaySender = relaySender;
        _settings = settings;
    }

    public string? InstanceName { get; set; }
    public bool IsRegistered { get; set; }
    public List<RelayDevice> Devices { get; set; } = new();
    public RelayPairingCode? ActiveCode { get; set; }

    public async Task OnGetAsync()
    {
        await LoadStateAsync();
    }

    public async Task<IActionResult> OnPostGenerateCodeAsync(string? label)
    {
        try
        {
            ActiveCode = await _relayClient.CreatePairingCodeAsync(label);
            TempData["PairingCode"] = ActiveCode.Code;
            TempData["PairingCodeExpires"] = ActiveCode.ExpiresAt.ToString("O");
            TempData["Success"] = $"Pairing code generated: {ActiveCode.Code}";
        }
        catch (Exception ex)
        {
            TempData["Error"] = $"Failed to generate pairing code: {ex.Message}";
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestDeviceAsync(string deviceId)
    {
        try
        {
            var devices = await _relayClient.GetDevicesAsync();
            var device = devices.FirstOrDefault(d => d.DeviceId == deviceId);
            var label = device?.Label ?? deviceId;

            var sent = await _relaySender.SendAsync(
                "Test Notification",
                $"This is a test push notification sent to {label}.",
                new Dictionary<string, string> { ["test"] = "true" }
            );

            if (sent)
                TempData["Success"] = $"Test notification sent to {label}.";
            else
                TempData["Error"] = $"Failed to send test notification to {label}.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = $"Failed to send test notification: {ex.Message}";
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRevokeDeviceAsync(string deviceId)
    {
        try
        {
            await _relayClient.RevokeDeviceAsync(deviceId);
            TempData["Success"] = "Device revoked.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = $"Failed to revoke device: {ex.Message}";
        }

        return RedirectToPage();
    }

    private async Task LoadStateAsync()
    {
        IsRegistered = await _settings.GetAsync("Relay:Registered") == "true";
        InstanceName = await _settings.GetAsync("Relay:InstanceName");

        if (IsRegistered)
        {
            try
            {
                Devices = await _relayClient.GetDevicesAsync();
            }
            catch
            {
                Devices = new List<RelayDevice>();
            }
        }

        // Restore active pairing code from TempData
        if (TempData.Peek("PairingCode") is string code && TempData.Peek("PairingCodeExpires") is string expiresStr)
        {
            if (DateTime.TryParse(expiresStr, out var expires) && expires > DateTime.UtcNow)
            {
                ActiveCode = new RelayPairingCode(code, expires);
            }
        }
    }
}
