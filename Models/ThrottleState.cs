namespace EventAlertService.Models;

public class ThrottleState
{
    public int Id { get; set; }
    public int FilterRuleId { get; set; }
    public int RecipientId { get; set; }
    public DateTime WindowStart { get; set; }
    public int SmsCount { get; set; }

    public FilterRule FilterRule { get; set; } = null!;
    public Recipient Recipient { get; set; } = null!;
}
