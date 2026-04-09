using HirschNotify.Data;
using HirschNotify.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace HirschNotify.Pages.Groups;

[Authorize]
public class EditModel : PageModel
{
    private readonly AppDbContext _db;

    public EditModel(AppDbContext db) => _db = db;

    public RecipientGroup Group { get; set; } = new();
    public bool IsNew => Group.Id == 0;
    public string? ErrorMessage { get; set; }
    public List<Recipient> AllRecipients { get; set; } = new();
    public List<int> SelectedMemberIds { get; set; } = new();

    public async Task OnGetAsync(int? id)
    {
        AllRecipients = await _db.Recipients.OrderBy(r => r.Name).ToListAsync();

        if (id.HasValue)
        {
            Group = await _db.RecipientGroups
                .Include(g => g.Members)
                .FirstOrDefaultAsync(g => g.Id == id.Value) ?? new();
            SelectedMemberIds = Group.Members.Select(m => m.RecipientId).ToList();
        }
    }

    public async Task<IActionResult> OnPostAsync(int id, string name, string? description, List<int> selectedMemberIds)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            ErrorMessage = "Name is required.";
            AllRecipients = await _db.Recipients.OrderBy(r => r.Name).ToListAsync();
            Group = new RecipientGroup { Id = id, Name = name ?? "", Description = description };
            SelectedMemberIds = selectedMemberIds;
            return Page();
        }

        RecipientGroup group;
        if (id > 0)
        {
            group = await _db.RecipientGroups.Include(g => g.Members).FirstOrDefaultAsync(g => g.Id == id) ?? new();
            group.Name = name;
            group.Description = description;
            group.UpdatedAt = DateTime.UtcNow;

            // Update members
            group.Members.Clear();
            foreach (var rid in selectedMemberIds)
            {
                group.Members.Add(new RecipientGroupMember { RecipientGroupId = group.Id, RecipientId = rid });
            }
        }
        else
        {
            group = new RecipientGroup
            {
                Name = name,
                Description = description,
            };
            foreach (var rid in selectedMemberIds)
            {
                group.Members.Add(new RecipientGroupMember { RecipientId = rid });
            }
            _db.RecipientGroups.Add(group);
        }

        await _db.SaveChangesAsync();
        TempData["Success"] = "Group saved.";
        return RedirectToPage("Index");
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
        return RedirectToPage("Index");
    }
}
