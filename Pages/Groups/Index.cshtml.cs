using HirschNotify.Data;
using HirschNotify.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace HirschNotify.Pages.Groups;

[Authorize]
public class IndexModel : PageModel
{
    private readonly AppDbContext _db;

    public IndexModel(AppDbContext db) => _db = db;

    public List<RecipientGroup> Groups { get; set; } = new();

    public async Task OnGetAsync()
    {
        Groups = await _db.RecipientGroups
            .Include(g => g.Members)
            .OrderBy(g => g.Name)
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var group = await _db.RecipientGroups.FindAsync(id);
        if (group != null)
        {
            _db.RecipientGroups.Remove(group);
            await _db.SaveChangesAsync();
            TempData["Success"] = "Group deleted.";
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostBulkDeleteAsync(int[] ids)
    {
        var groups = await _db.RecipientGroups.Where(g => ids.Contains(g.Id)).ToListAsync();
        _db.RecipientGroups.RemoveRange(groups);
        await _db.SaveChangesAsync();
        TempData["Success"] = $"{groups.Count} groups deleted.";
        return RedirectToPage();
    }
}
