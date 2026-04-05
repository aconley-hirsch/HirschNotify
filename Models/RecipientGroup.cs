namespace EventAlertService.Models;

public class RecipientGroup
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<RecipientGroupMember> Members { get; set; } = new();
    public List<FilterRuleRecipientGroup> FilterRuleRecipientGroups { get; set; } = new();
}
