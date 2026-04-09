namespace HirschNotify.Services;

public interface IWebSocketAuthService
{
    Task<string?> GetTokenAsync(string loginUrl, string username, string password,
        string usernameField, string passwordField, string tokenField,
        Dictionary<string, string>? additionalFields = null);
}
