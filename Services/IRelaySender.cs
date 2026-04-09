namespace HirschNotify.Services;

public interface IRelaySender
{
    Task<bool> SendAsync(string title, string body, Dictionary<string, string>? data = null, int? recipientId = null);
}
