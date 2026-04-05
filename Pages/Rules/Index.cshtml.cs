using EventAlertService.Data;
using EventAlertService.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace EventAlertService.Pages.Rules;

[Authorize]
public class IndexModel : PageModel
{
    private readonly AppDbContext _db;

    public IndexModel(AppDbContext db) => _db = db;

    public List<FilterRule> Rules { get; set; } = new();

    public async Task OnGetAsync()
    {
        Rules = await _db.FilterRules
            .Include(r => r.Conditions)
            .Include(r => r.FilterRuleRecipients)
                .ThenInclude(fr => fr.Recipient)
            .Include(r => r.FilterRuleRecipientGroups)
                .ThenInclude(fg => fg.RecipientGroup)
            .OrderBy(r => r.Name)
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostToggleActiveAsync(int id)
    {
        var rule = await _db.FilterRules.FindAsync(id);
        if (rule != null)
        {
            rule.IsActive = !rule.IsActive;
            rule.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var rule = await _db.FilterRules.FindAsync(id);
        if (rule != null)
        {
            _db.FilterRules.Remove(rule);
            await _db.SaveChangesAsync();
            TempData["Success"] = "Rule deleted.";
        }
        return RedirectToPage();
    }
}
