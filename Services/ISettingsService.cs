namespace HirschNotify.Services;

public interface ISettingsService
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value);
    Task<string?> GetEncryptedAsync(string key);
    Task SetEncryptedAsync(string key, string value);
    Task<Dictionary<string, string>> GetAllAsync();
}
