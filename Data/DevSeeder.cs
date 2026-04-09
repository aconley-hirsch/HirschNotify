using HirschNotify.Models;
using HirschNotify.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace HirschNotify.Data;

public static class DevSeeder
{
    public static async Task SeedAsync(IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var userManager = services.GetRequiredService<UserManager<IdentityUser>>();
        var settingsService = services.GetRequiredService<ISettingsService>();

        // Admin user
        if (!userManager.Users.Any())
        {
            var user = new IdentityUser { UserName = "admin" };
            await userManager.CreateAsync(user, "admin1");
        }

        // WebSocket settings
        await settingsService.SetAsync("WebSocket:Url", "ws://api.velocity.lan/websocket");
        await settingsService.SetAsync("WebSocket:LoginUrl", "http://api.velocity.lan/webapi/Login");
        await settingsService.SetAsync("WebSocket:Username", "administrator");
        await settingsService.SetEncryptedAsync("WebSocket:Password", "Hirsch123!");
        await settingsService.SetAsync("WebSocket:UsernameField", "UserName");
        await settingsService.SetAsync("WebSocket:PasswordField", "Password");
        await settingsService.SetAsync("WebSocket:TokenField", "Token");
        await settingsService.SetAsync("WebSocket:ReconnectBaseDelaySec", "5");
        await settingsService.SetAsync("WebSocket:ReconnectMaxDelaySec", "300");
        await settingsService.SetAsync("WebSocket:DisconnectAlertSec", "120");

        // Recipient
        if (!await db.Recipients.AnyAsync())
        {
            var recipient = new Recipient
            {
                Name = "Arick",
                IsActive = true
            };
            db.Recipients.Add(recipient);
            await db.SaveChangesAsync();

            // Filter rule
            var rule = new FilterRule
            {
                Name = "Forced Door",
                Description = "Forced entry alerts",
                LogicOperator = "AND",
                ThrottleMaxSms = 5,
                ThrottleWindowMinutes = 10,
                MessageTemplate = "{ruleName}: {description}\nType: {eventType}\nTime: {localtime}",
                IsActive = true
            };
            rule.Conditions.Add(new FilterCondition
            {
                FieldPath = "eventID",
                Operator = "equals",
                Value = "5001",
                SortOrder = 0
            });
            rule.FilterRuleRecipients.Add(new FilterRuleRecipient { RecipientId = recipient.Id });
            db.FilterRules.Add(rule);
            await db.SaveChangesAsync();
        }
    }
}
