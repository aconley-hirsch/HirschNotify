namespace EventAlertService.Services;

public interface IThrottleManager
{
    Task<bool> ShouldSendAsync(int filterRuleId, int recipientId, int maxSms, int windowMinutes);
}
