using System.Text.Json;
using HirschNotify.Data;
using HirschNotify.Models;
using Microsoft.EntityFrameworkCore;

namespace HirschNotify.Services;

public class FilterEngine : IFilterEngine
{
    private readonly AppDbContext _db;
    private readonly ILogger<FilterEngine> _logger;

    public FilterEngine(AppDbContext db, ILogger<FilterEngine> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<List<FilterRule>> EvaluateAsync(JsonElement eventData)
    {
        var rules = await _db.FilterRules
            .Where(r => r.IsActive)
            .Include(r => r.Conditions.OrderBy(c => c.SortOrder))
            .Include(r => r.FilterRuleRecipients)
                .ThenInclude(fr => fr.Recipient)
            .Include(r => r.FilterRuleRecipientGroups)
                .ThenInclude(fg => fg.RecipientGroup)
                    .ThenInclude(g => g.Members)
                        .ThenInclude(m => m.Recipient)
            .ToListAsync();

        var matched = new List<FilterRule>();

        foreach (var rule in rules)
        {
            if (rule.Conditions.Count == 0) continue;

            bool ruleMatches = rule.LogicOperator == "AND"
                ? rule.Conditions.All(c => EvaluateCondition(c, eventData))
                : rule.Conditions.Any(c => EvaluateCondition(c, eventData));

            if (ruleMatches)
            {
                _logger.LogInformation("Rule '{RuleName}' matched event", rule.Name);
                matched.Add(rule);
            }
        }

        return matched;
    }

    private bool EvaluateCondition(FilterCondition condition, JsonElement eventData)
    {
        var fieldValue = ResolveFieldPath(eventData, condition.FieldPath);
        if (fieldValue == null) return false;

        var actual = fieldValue.Value.ToString() ?? "";
        var expected = condition.Value;

        return condition.Operator switch
        {
            "equals" => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
            "not_equals" => !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
            "contains" => actual.Contains(expected, StringComparison.OrdinalIgnoreCase),
            "greater_than" => CompareNumeric(actual, expected) > 0,
            "less_than" => CompareNumeric(actual, expected) < 0,
            _ => false
        };
    }

    private static JsonElement? ResolveFieldPath(JsonElement element, string path)
    {
        var segments = path.Split('.');
        var current = element;

        foreach (var segment in segments)
        {
            if (current.ValueKind != JsonValueKind.Object) return null;
            if (!TryGetPropertyIgnoreCase(current, segment, out var next)) return null;
            current = next;
        }

        return current;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value)) return true;
        foreach (var prop in element.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static int CompareNumeric(string actual, string expected)
    {
        if (double.TryParse(actual, out var a) && double.TryParse(expected, out var b))
            return a.CompareTo(b);
        return string.Compare(actual, expected, StringComparison.OrdinalIgnoreCase);
    }
}
