using EventAlertService.Data;
using EventAlertService.Models;
using EventAlertService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EventAlertService.Pages.Recipients;

[Authorize]
public class EditModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly IRelayClient _relayClient;
    private readonly INotificationSender _notificationSender;

    public EditModel(AppDbContext db, IRelayClient relayClient, INotificationSender notificationSender)
    {
        _db = db;
        _relayClient = relayClient;
        _notificationSender = notificationSender;
    }

    public Recipient Recipient { get; set; } = new();
    public bool IsNew => Recipient.Id == 0;
    public string? ErrorMessage { get; set; }
    public RelayDevice? PairedDevice { get; set; }
    public RelayPairingCode? PairingCode { get; set; }

    public async Task OnGetAsync(int? id)
    {
        if (id.HasValue)
        {
            Recipient = await _db.Recipients.FindAsync(id.Value) ?? new Recipient();
            await LoadPairedDevice();
        }

        if (TempData.Peek("EditPairingCode") is string code &&
            TempData.Peek("EditPairingCodeExpires") is string expiresStr)
        {
            if (DateTime.TryParse(expiresStr, out var expires) && expires > DateTime.UtcNow)
            {
                PairingCode = new RelayPairingCode(code, expires);
            }
        }
    }

    private async Task LoadPairedDevice()
    {
        if (Recipient.Id == 0) return;
        try
        {
            var devices = await _relayClient.GetDevicesAsync();
            PairedDevice = devices.FirstOrDefault(d => d.RecipientId == Recipient.Id);
        }
        catch { }
    }

    public async Task<IActionResult> OnPostAsync(int id, string name, bool isActive = false)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            ErrorMessage = "Name is required.";
            Recipient = new Recipient { Id = id, Name = name ?? "", IsActive = isActive };
            return Page();
        }

        Recipient recipient;
        if (id > 0)
        {
            recipient = await _db.Recipients.FindAsync(id) ?? new Recipient();
            recipient.Name = name;
            recipient.IsActive = isActive;
            recipient.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            recipient = new Recipient
            {
                Name = name,
                IsActive = isActive
            };
            _db.Recipients.Add(recipient);
        }

        await _db.SaveChangesAsync();
        TempData["Success"] = "Recipient saved.";
        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostTestNotificationAsync(int id)
    {
        var recipient = await _db.Recipients.FindAsync(id);
        if (recipient == null)
        {
            TempData["Error"] = "Recipient not found.";
            return RedirectToPage(new { id });
        }

        var sent = await _notificationSender.SendAsync(recipient, $"Test notification for {recipient.Name}.");
        if (sent)
            TempData["Success"] = $"Test notification sent to {recipient.Name}.";
        else
            TempData["Error"] = $"Failed to send test notification to {recipient.Name}. Check relay registration and device pairing.";

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostUnpairDeviceAsync(string deviceId, int recipientId)
    {
        try
        {
            await _relayClient.RevokeDeviceAsync(deviceId);
            TempData["Success"] = "Device unpaired. Generate a new pairing code to re-pair.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = $"Failed to unpair device: {ex.Message}";
        }

        return RedirectToPage(new { id = recipientId });
    }

    public async Task<IActionResult> OnPostPairDeviceAsync(int recipientId)
    {
        var recipient = await _db.Recipients.FindAsync(recipientId);
        if (recipient == null)
        {
            TempData["Error"] = "Recipient not found.";
            return RedirectToPage(new { id = recipientId });
        }

        try
        {
            var code = await _relayClient.CreatePairingCodeAsync(recipient.Name, recipientId);
            TempData["EditPairingCode"] = code.Code;
            TempData["EditPairingCodeExpires"] = code.ExpiresAt.ToString("O");
            TempData["Success"] = $"Pairing code generated: {code.Code}";
        }
        catch (Exception ex)
        {
            TempData["Error"] = $"Failed to generate pairing code: {ex.Message}";
        }

        return RedirectToPage(new { id = recipientId });
    }

    public async Task<IActionResult> OnPostToggleActiveAsync(int id)
    {
        var recipient = await _db.Recipients.FindAsync(id);
        if (recipient != null)
        {
            recipient.IsActive = !recipient.IsActive;
            recipient.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            TempData["Success"] = $"Recipient {(recipient.IsActive ? "activated" : "deactivated")}.";
        }
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var recipient = await _db.Recipients.FindAsync(id);
        if (recipient != null)
        {
            _db.Recipients.Remove(recipient);
            await _db.SaveChangesAsync();
            TempData["Success"] = "Recipient deleted.";
        }
        return RedirectToPage("Index");
    }
}
