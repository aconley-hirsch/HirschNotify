using EventAlertService.Models;

namespace EventAlertService.Services;

public interface INotificationSender
{
    Task<bool> SendAsync(Recipient recipient, string message);
}
