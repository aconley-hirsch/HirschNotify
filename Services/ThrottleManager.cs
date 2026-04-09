using HirschNotify.Data;
using HirschNotify.Models;
using Microsoft.EntityFrameworkCore;

namespace HirschNotify.Services;

public class ThrottleManager : IThrottleManager
{
    private readonly AppDbContext _db;

    public ThrottleManager(AppDbContext db)
    {
        _db = db;
    }

    public async Task<bool> ShouldSendAsync(int filterRuleId, int recipientId, int maxSms, int windowMinutes)
    {
        var now = DateTime.UtcNow;
        var state = await _db.ThrottleStates
            .FirstOrDefaultAsync(t => t.FilterRuleId == filterRuleId && t.RecipientId == recipientId);

        if (state == null || state.WindowStart.AddMinutes(windowMinutes) < now)
        {
            if (state == null)
            {
                state = new ThrottleState
                {
                    FilterRuleId = filterRuleId,
                    RecipientId = recipientId,
                    WindowStart = now,
                    SmsCount = 1
                };
                _db.ThrottleStates.Add(state);
            }
            else
            {
                state.WindowStart = now;
                state.SmsCount = 1;
            }
            await _db.SaveChangesAsync();
            return true;
        }

        if (state.SmsCount < maxSms)
        {
            state.SmsCount++;
            await _db.SaveChangesAsync();
            return true;
        }

        return false;
    }
}
