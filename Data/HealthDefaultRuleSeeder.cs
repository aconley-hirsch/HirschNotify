using HirschNotify.Models;
using HirschNotify.Services;
using Microsoft.EntityFrameworkCore;

namespace HirschNotify.Data;

/// <summary>
/// On first boot, create a default FilterRule that matches critical Windows
/// service health events so operators get paged when a monitored service
/// drops without having to hand-author a rule. Guarded by a
/// <c>Installation:HealthDefaultRuleCreated</c> settings flag so a user who
/// deletes the rule doesn't get it recreated on every restart.
/// </summary>
public static class HealthDefaultRuleSeeder
{
    private const string CreatedFlagKey = "Installation:HealthDefaultRuleCreated";

    public static async Task EnsureCreatedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        if (await settings.GetAsync(CreatedFlagKey) == "true")
            return;

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var rule = new FilterRule
        {
            Name = "Windows service health",
            Description = "Alerts when a monitored Windows service enters a Critical state (an Automatic service observed Stopped). Attach recipients to start receiving notifications. Adjust monitored services on the Health page.",
            LogicOperator = "AND",
            ThrottleMaxSms = 5,
            ThrottleWindowMinutes = 15,
            MessageTemplate = "{ruleName}\n{displayName} ({serviceName})\n{previousStatus} → {status}\n{localtime}",
            IsActive = true,
        };

        rule.Conditions.Add(new FilterCondition
        {
            FieldPath = "source",
            Operator = "equals",
            Value = "WindowsService",
            SortOrder = 0,
        });

        rule.Conditions.Add(new FilterCondition
        {
            FieldPath = "severity",
            Operator = "equals",
            Value = "critical",
            SortOrder = 1,
        });

        db.FilterRules.Add(rule);
        await db.SaveChangesAsync();

        await settings.SetAsync(CreatedFlagKey, "true");
    }
}
