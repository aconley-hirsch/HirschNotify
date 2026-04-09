namespace HirschNotify.Models;

public class FilterRuleRecipient
{
    public int FilterRuleId { get; set; }
    public int RecipientId { get; set; }

    public FilterRule FilterRule { get; set; } = null!;
    public Recipient Recipient { get; set; } = null!;
}
