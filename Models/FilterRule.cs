namespace HirschNotify.Models;

public class FilterRule
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public string LogicOperator { get; set; } = "AND"; // AND or OR
    public int ThrottleMaxSms { get; set; } = 5;
    public int ThrottleWindowMinutes { get; set; } = 10;
    public string? MessageTemplate { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<FilterCondition> Conditions { get; set; } = new();
    public List<FilterRuleRecipient> FilterRuleRecipients { get; set; } = new();
    public List<FilterRuleRecipientGroup> FilterRuleRecipientGroups { get; set; } = new();
}
