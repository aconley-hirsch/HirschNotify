namespace EventAlertService.Models;

public class RecipientGroupMember
{
    public int RecipientGroupId { get; set; }
    public int RecipientId { get; set; }

    public RecipientGroup RecipientGroup { get; set; } = null!;
    public Recipient Recipient { get; set; } = null!;
}
