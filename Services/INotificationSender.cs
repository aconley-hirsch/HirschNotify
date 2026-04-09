using HirschNotify.Models;

namespace HirschNotify.Services;

public interface INotificationSender
{
    Task<bool> SendAsync(Recipient recipient, string message);
}
