namespace HirschNotify.Models;

public class FilterRuleRecipientGroup
{
    public int FilterRuleId { get; set; }
    public int RecipientGroupId { get; set; }

    public FilterRule FilterRule { get; set; } = null!;
    public RecipientGroup RecipientGroup { get; set; } = null!;
}
