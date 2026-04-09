using HirschNotify.Data;
using HirschNotify.Models;
using HirschNotify.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace HirschNotify.Pages.Recipients;

[Authorize]
public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly INotificationSender _notificationSender;
    private readonly IRelayClient _relayClient;

    public IndexModel(AppDbContext db, INotificationSender notificationSender, IRelayClient relayClient)
    {
        _db = db;
        _notificationSender = notificationSender;
        _relayClient = relayClient;
    }

    public List<Recipient> Recipients { get; set; } = new();
    public List<RelayDevice> PairedDevices { get; set; } = new();
    public RelayPairingCode? PairingCode { get; set; }
    public string? PairingRecipientName { get; set; }

    public bool RecipientHasDevice(int recipientId) =>
        PairedDevices.Any(d => d.RecipientId == recipientId);

    public RelayDevice? GetDeviceForRecipient(int recipientId) =>
        PairedDevices.FirstOrDefault(d => d.RecipientId == recipientId);

    public async Task OnGetAsync()
    {
        Recipients = await _db.Recipients.OrderBy(r => r.Name).ToListAsync();

        try { PairedDevices = await _relayClient.GetDevicesAsync(); }
        catch { PairedDevices = new(); }

        // Restore pairing code from TempData
        if (TempData.Peek("PairingCode") is string code &&
            TempData.Peek("PairingCodeExpires") is string expiresStr &&
            TempData.Peek("PairingRecipientName") is string name)
        {
            if (DateTime.TryParse(expiresStr, out var expires) && expires > DateTime.UtcNow)
            {
                PairingCode = new RelayPairingCode(code, expires);
                PairingRecipientName = name;
            }
        }
    }

    public async Task<IActionResult> OnPostPairDeviceAsync(int recipientId, string recipientName)
    {
        try
        {
            var code = await _relayClient.CreatePairingCodeAsync(recipientName, recipientId);
            TempData["PairingCode"] = code.Code;
            TempData["PairingCodeExpires"] = code.ExpiresAt.ToString("O");
            TempData["PairingRecipientName"] = recipientName;
            TempData["Success"] = $"Pairing code generated for {recipientName}: {code.Code}";
        }
        catch (Exception ex)
        {
            TempData["Error"] = $"Failed to generate pairing code: {ex.Message}";
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostToggleActiveAsync(int id)
    {
        var recipient = await _db.Recipients.FindAsync(id);
        if (recipient != null)
        {
            recipient.IsActive = !recipient.IsActive;
            recipient.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
        return RedirectToPage();
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
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostBulkActivateAsync(int[] ids)
    {
        var recipients = await _db.Recipients.Where(r => ids.Contains(r.Id)).ToListAsync();
        foreach (var r in recipients) { r.IsActive = true; r.UpdatedAt = DateTime.UtcNow; }
        await _db.SaveChangesAsync();
        TempData["Success"] = $"{recipients.Count} recipients activated.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostBulkDeactivateAsync(int[] ids)
    {
        var recipients = await _db.Recipients.Where(r => ids.Contains(r.Id)).ToListAsync();
        foreach (var r in recipients) { r.IsActive = false; r.UpdatedAt = DateTime.UtcNow; }
        await _db.SaveChangesAsync();
        TempData["Success"] = $"{recipients.Count} recipients deactivated.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostBulkDeleteAsync(int[] ids)
    {
        var recipients = await _db.Recipients.Where(r => ids.Contains(r.Id)).ToListAsync();
        _db.Recipients.RemoveRange(recipients);
        await _db.SaveChangesAsync();
        TempData["Success"] = $"{recipients.Count} recipients deleted.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestNotificationAsync()
    {
        var active = await _db.Recipients.Where(r => r.IsActive).ToListAsync();
        if (!active.Any())
        {
            TempData["Success"] = "No active recipients.";
            return RedirectToPage();
        }

        var sent = 0;
        foreach (var r in active)
        {
            if (await _notificationSender.SendAsync(r, "Test notification from Hirsch Notify."))
                sent++;
        }

        TempData["Success"] = $"Test notification sent to {sent} of {active.Count} active recipients.";
        return RedirectToPage();
    }
}
