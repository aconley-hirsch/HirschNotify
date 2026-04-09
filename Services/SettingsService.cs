using HirschNotify.Data;
using HirschNotify.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace HirschNotify.Services;

public class SettingsService : ISettingsService
{
    private readonly AppDbContext _db;
    private readonly IDataProtector _protector;

    public SettingsService(AppDbContext db, IDataProtectionProvider dataProtectionProvider)
    {
        _db = db;
        _protector = dataProtectionProvider.CreateProtector("HirschNotify.Settings");
    }

    public async Task<string?> GetAsync(string key)
    {
        var setting = await _db.AppSettings.FindAsync(key);
        return setting?.Value;
    }

    public async Task SetAsync(string key, string value)
    {
        var setting = await _db.AppSettings.FindAsync(key);
        if (setting == null)
        {
            setting = new AppSetting { Key = key, Value = value };
            _db.AppSettings.Add(setting);
        }
        else
        {
            setting.Value = value;
        }
        await _db.SaveChangesAsync();
    }

    public async Task<string?> GetEncryptedAsync(string key)
    {
        var setting = await _db.AppSettings.FindAsync(key);
        if (setting?.Value is null or "") return null;
        try
        {
            return _protector.Unprotect(setting.Value);
        }
        catch
        {
            return null;
        }
    }

    public async Task SetEncryptedAsync(string key, string value)
    {
        var encrypted = _protector.Protect(value);
        await SetAsync(key, encrypted);
    }

    public async Task<Dictionary<string, string>> GetAllAsync()
    {
        return await _db.AppSettings.ToDictionaryAsync(s => s.Key, s => s.Value);
    }
}
