namespace HirschNotify.Models;

public class FilterCondition
{
    public int Id { get; set; }
    public int FilterRuleId { get; set; }
    public string FieldPath { get; set; } = string.Empty;
    public string Operator { get; set; } = "equals"; // equals, not_equals, contains, greater_than, less_than
    public string Value { get; set; } = string.Empty;
    public int SortOrder { get; set; }

    public FilterRule FilterRule { get; set; } = null!;
}
