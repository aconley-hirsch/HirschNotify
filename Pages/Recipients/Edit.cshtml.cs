using HirschNotify.Data;
using HirschNotify.Models;
using HirschNotify.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace HirschNotify.Pages.Recipients;

[Authorize]
public class EditModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly IRelayClient _relayClient;
    private readonly INotificationSender _notificationSender;
    private readonly IEnumerable<IContactMethodSender> _contactMethodSenders;
    private readonly ILogger<EditModel> _logger;

    public EditModel(AppDbContext db, IRelayClient relayClient, INotificationSender notificationSender, IEnumerable<IContactMethodSender> contactMethodSenders, ILogger<EditModel> logger)
    {
        _db = db;
        _relayClient = relayClient;
        _notificationSender = notificationSender;
        _contactMethodSenders = contactMethodSenders;
        _logger = logger;
    }

    public Recipient Recipient { get; set; } = new();
    public bool IsNew => Recipient.Id == 0;
    public string? ErrorMessage { get; set; }
    public RelayDevice? PairedDevice { get; set; }
    public RelayPairingCode? PairingCode { get; set; }
    public List<ContactMethod> ContactMethods { get; set; } = new();
    public List<string> AvailableContactMethodTypes { get; set; } = new();

    public async Task OnGetAsync(int? id)
    {
        AvailableContactMethodTypes = _contactMethodSenders.Select(s => s.Type).ToList();

        if (id.HasValue)
        {
            Recipient = await _db.Recipients.FindAsync(id.Value) ?? new Recipient();
            ContactMethods = await _db.ContactMethods
                .Where(cm => cm.RecipientId == id.Value)
                .OrderBy(cm => cm.Type).ThenBy(cm => cm.Label)
                .ToListAsync();
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
        catch (Exception ex)
        {
            // Relay may be unregistered, unreachable, or returning auth
            // errors — any of which are valid "no paired device to show"
            // states. Log for diagnostics but don't surface to the user.
            _logger.LogDebug(ex, "Could not fetch paired device for recipient {RecipientId} from relay", Recipient.Id);
        }
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
        var recipient = await _db.Recipients
            .Include(r => r.ContactMethods)
            .FirstOrDefaultAsync(r => r.Id == id);
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

    public async Task<IActionResult> OnPostAddContactMethodAsync(int recipientId, string type, string label, string configuration)
    {
        var sender = _contactMethodSenders.FirstOrDefault(s => string.Equals(s.Type, type, StringComparison.OrdinalIgnoreCase));
        if (sender == null)
        {
            TempData["Error"] = "Unknown contact method type.";
            return RedirectToPage(new { id = recipientId });
        }

        // For email, wrap raw address into JSON config
        if (type == "email" && !configuration.TrimStart().StartsWith('{'))
        {
            configuration = System.Text.Json.JsonSerializer.Serialize(new { address = configuration });
        }

        var error = sender.ValidateConfiguration(configuration);
        if (error != null)
        {
            TempData["Error"] = error;
            return RedirectToPage(new { id = recipientId });
        }

        var cm = new ContactMethod
        {
            RecipientId = recipientId,
            Type = type,
            Label = string.IsNullOrWhiteSpace(label) ? type : label,
            Configuration = configuration,
            IsActive = true
        };
        _db.ContactMethods.Add(cm);
        await _db.SaveChangesAsync();
        TempData["Success"] = "Contact method added.";
        return RedirectToPage(new { id = recipientId });
    }

    public async Task<IActionResult> OnPostRemoveContactMethodAsync(int contactMethodId, int recipientId)
    {
        var cm = await _db.ContactMethods.FindAsync(contactMethodId);
        if (cm != null)
        {
            _db.ContactMethods.Remove(cm);
            await _db.SaveChangesAsync();
        }
        TempData["Success"] = "Contact method removed.";
        return RedirectToPage(new { id = recipientId });
    }

    public async Task<IActionResult> OnPostToggleContactMethodAsync(int contactMethodId, int recipientId)
    {
        var cm = await _db.ContactMethods.FindAsync(contactMethodId);
        if (cm != null)
        {
            cm.IsActive = !cm.IsActive;
            cm.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
        return RedirectToPage(new { id = recipientId });
    }

    public async Task<IActionResult> OnPostTestContactMethodAsync(int contactMethodId, int recipientId)
    {
        var cm = await _db.ContactMethods.FindAsync(contactMethodId);
        if (cm == null)
        {
            TempData["Error"] = "Contact method not found.";
            return RedirectToPage(new { id = recipientId });
        }

        var sender = _contactMethodSenders.FirstOrDefault(s => string.Equals(s.Type, cm.Type, StringComparison.OrdinalIgnoreCase));
        if (sender == null)
        {
            TempData["Error"] = $"No sender registered for type '{cm.Type}'.";
            return RedirectToPage(new { id = recipientId });
        }

        var sent = await sender.SendAsync(cm, "Test", "Test notification from HirschNotify.");
        TempData[sent ? "Success" : "Error"] = sent
            ? "Test sent successfully."
            : "Test failed. Check SMTP settings and contact method configuration.";
        return RedirectToPage(new { id = recipientId });
    }
}
