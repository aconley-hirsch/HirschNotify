using System.Text.Json;
using HirschNotify.Data;
using HirschNotify.Models;
using HirschNotify.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace HirschNotify.Pages.Rules;

[Authorize]
public class EditModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly ConnectionState _connectionState;

    public EditModel(AppDbContext db, ConnectionState connectionState)
    {
        _db = db;
        _connectionState = connectionState;
    }

    public List<FieldInfo> CommonFields => EventSchema.CommonFields;
    public Dictionary<string, List<FieldInfo>> SourceFields => EventSchema.SourceFields;
    public Dictionary<string, string> OperatorLabels => EventSchema.OperatorLabels;
    public List<RecentEvent> RecentSamples { get; set; } = new();

    public int RuleId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string LogicOperator { get; set; } = "AND";
    public int ThrottleMaxSms { get; set; } = 5;
    public int ThrottleWindowMinutes { get; set; } = 10;
    public string? MessageTemplate { get; set; }
    public bool IsActive { get; set; } = true;
    public List<ConditionInput> Conditions { get; set; } = new();
    public List<int> SelectedRecipientIds { get; set; } = new();
    public List<int> SelectedGroupIds { get; set; } = new();
    public List<Recipient> AllRecipients { get; set; } = new();
    public List<RecipientGroup> AllGroups { get; set; } = new();
    public bool IsNew => RuleId == 0;
    public string? ErrorMessage { get; set; }

    public class ConditionInput
    {
        public string FieldPath { get; set; } = "";
        public string Operator { get; set; } = "equals";
        public string Value { get; set; } = "";
    }

    public async Task OnGetAsync(int? id)
    {
        AllRecipients = await _db.Recipients.OrderBy(r => r.Name).ToListAsync();
        AllGroups = await _db.RecipientGroups.Include(g => g.Members).OrderBy(g => g.Name).ToListAsync();

        if (id.HasValue)
        {
            var rule = await _db.FilterRules
                .Include(r => r.Conditions)
                .Include(r => r.FilterRuleRecipients)
                .Include(r => r.FilterRuleRecipientGroups)
                .FirstOrDefaultAsync(r => r.Id == id.Value);

            if (rule != null)
            {
                RuleId = rule.Id;
                Name = rule.Name;
                Description = rule.Description;
                LogicOperator = rule.LogicOperator;
                ThrottleMaxSms = rule.ThrottleMaxSms;
                ThrottleWindowMinutes = rule.ThrottleWindowMinutes;
                MessageTemplate = rule.MessageTemplate;
                IsActive = rule.IsActive;
                Conditions = rule.Conditions.OrderBy(c => c.SortOrder).Select(c => new ConditionInput
                {
                    FieldPath = c.FieldPath,
                    Operator = c.Operator,
                    Value = c.Value
                }).ToList();
                SelectedRecipientIds = rule.FilterRuleRecipients.Select(fr => fr.RecipientId).ToList();
                SelectedGroupIds = rule.FilterRuleRecipientGroups.Select(fg => fg.RecipientGroupId).ToList();
            }
        }

        if (Conditions.Count == 0)
            Conditions.Add(new ConditionInput());

        RecentSamples = _connectionState.GetRecentEvents(5);
    }

    public async Task<IActionResult> OnPostAsync(int ruleId, string name, string? description, string logicOperator,
        int throttleMaxSms, int throttleWindowMinutes, string? messageTemplate, bool isActive, List<ConditionInput> conditions, List<int> selectedRecipientIds, List<int> selectedGroupIds)
    {
        AllRecipients = await _db.Recipients.OrderBy(r => r.Name).ToListAsync();
        AllGroups = await _db.RecipientGroups.Include(g => g.Members).OrderBy(g => g.Name).ToListAsync();

        if (string.IsNullOrWhiteSpace(name))
        {
            ErrorMessage = "Rule name is required.";
            RuleId = ruleId;
            Name = name ?? "";
            Description = description;
            LogicOperator = logicOperator;
            ThrottleMaxSms = throttleMaxSms;
            ThrottleWindowMinutes = throttleWindowMinutes;
            MessageTemplate = messageTemplate;
            IsActive = isActive;
            Conditions = conditions;
            SelectedRecipientIds = selectedRecipientIds;
            SelectedGroupIds = selectedGroupIds;
            return Page();
        }

        FilterRule rule;
        if (ruleId > 0)
        {
            rule = await _db.FilterRules
                .Include(r => r.Conditions)
                .Include(r => r.FilterRuleRecipients)
                .Include(r => r.FilterRuleRecipientGroups)
                .FirstAsync(r => r.Id == ruleId);

            rule.Name = name;
            rule.Description = description;
            rule.LogicOperator = logicOperator;
            rule.ThrottleMaxSms = throttleMaxSms;
            rule.ThrottleWindowMinutes = throttleWindowMinutes;
            rule.MessageTemplate = messageTemplate;
            rule.IsActive = isActive;
            rule.UpdatedAt = DateTime.UtcNow;

            _db.FilterConditions.RemoveRange(rule.Conditions);
            _db.FilterRuleRecipients.RemoveRange(rule.FilterRuleRecipients);
            _db.FilterRuleRecipientGroups.RemoveRange(rule.FilterRuleRecipientGroups);
        }
        else
        {
            rule = new FilterRule
            {
                Name = name,
                Description = description,
                LogicOperator = logicOperator,
                ThrottleMaxSms = throttleMaxSms,
                ThrottleWindowMinutes = throttleWindowMinutes,
                MessageTemplate = messageTemplate,
                IsActive = isActive
            };
            _db.FilterRules.Add(rule);
        }

        var validConditions = conditions.Where(c => !string.IsNullOrWhiteSpace(c.FieldPath)).ToList();
        for (var i = 0; i < validConditions.Count; i++)
        {
            rule.Conditions.Add(new FilterCondition
            {
                FieldPath = validConditions[i].FieldPath,
                Operator = validConditions[i].Operator,
                Value = validConditions[i].Value,
                SortOrder = i
            });
        }

        foreach (var recipientId in selectedRecipientIds)
        {
            rule.FilterRuleRecipients.Add(new FilterRuleRecipient { RecipientId = recipientId });
        }

        foreach (var groupId in selectedGroupIds)
        {
            rule.FilterRuleRecipientGroups.Add(new FilterRuleRecipientGroup { RecipientGroupId = groupId });
        }

        await _db.SaveChangesAsync();
        TempData["Success"] = "Rule saved.";
        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostDeleteAsync(int ruleId)
    {
        var rule = await _db.FilterRules.FindAsync(ruleId);
        if (rule != null)
        {
            _db.FilterRules.Remove(rule);
            await _db.SaveChangesAsync();
            TempData["Success"] = "Rule deleted.";
        }
        return RedirectToPage("Index");
    }

    // ── Live Match Preview ────────────────────────────────────
    // Evaluates unsaved conditions against the in-memory ring buffer of recent events.
    // HTMX calls this on every condition edit with 300ms debounce.

    public class TestMatchResult
    {
        public int TotalEvents { get; set; }
        public int MatchedCount { get; set; }
        public List<EventMatchRow> Events { get; set; } = new();
        public bool HasConditions { get; set; }
        public string? PreviewTitle { get; set; }
        public string? PreviewBody { get; set; }
        public string? PreviewBasis { get; set; }
    }

    public class EventMatchRow
    {
        public DateTime Timestamp { get; set; }
        public string Source { get; set; } = "";
        public string Preview { get; set; } = "";
        public bool Matched { get; set; }
        public string RawJson { get; set; } = "";
        public List<ConditionResult> ConditionResults { get; set; } = new();
    }

    public class ConditionResult
    {
        public string FieldPath { get; set; } = "";
        public string Operator { get; set; } = "";
        public string ExpectedValue { get; set; } = "";
        public string? ActualValue { get; set; }
        public bool Passed { get; set; }
    }

    public IActionResult OnGetFieldValues(string? field)
    {
        if (string.IsNullOrWhiteSpace(field))
            return Content("", "text/html");

        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var evt in _connectionState.GetRecentEvents(100))
        {
            try
            {
                using var doc = JsonDocument.Parse(evt.RawJson);
                var found = ResolveFieldPath(doc.RootElement, field);
                if (found.HasValue && found.Value.ValueKind != JsonValueKind.Null)
                {
                    var str = found.Value.ToString();
                    if (!string.IsNullOrEmpty(str)) values.Add(str);
                }
            }
            catch (JsonException) { }
        }

        var type = EventSchema.GetFieldType(field);
        var operators = EventSchema.OperatorsForType(type);

        var result = new FieldValuesResult
        {
            FieldName = field,
            FieldType = type,
            Values = values.OrderBy(v => v).Take(50).ToList(),
            AllowedOperators = operators
        };

        return Partial("_FieldValues", result);
    }

    public class FieldValuesResult
    {
        public string FieldName { get; set; } = "";
        public string FieldType { get; set; } = "string";
        public List<string> Values { get; set; } = new();
        public List<string> AllowedOperators { get; set; } = new();
    }

    public IActionResult OnPostTestMatch(string? logicOperator, string? messageTemplate, string? name, List<ConditionInput>? conditions)
    {
        var op = logicOperator ?? "AND";
        var conds = (conditions ?? new List<ConditionInput>())
            .Where(c => !string.IsNullOrWhiteSpace(c.FieldPath) && !string.IsNullOrWhiteSpace(c.Value))
            .ToList();

        var recent = _connectionState.GetRecentEvents(100);

        var result = new TestMatchResult
        {
            TotalEvents = recent.Count,
            HasConditions = conds.Count > 0
        };

        EventMatchRow? firstMatch = null;
        EventMatchRow? firstEvent = null;

        foreach (var evt in recent)
        {
            var row = new EventMatchRow
            {
                Timestamp = evt.Timestamp,
                Preview = evt.Preview ?? "",
                RawJson = evt.RawJson,
                Source = ExtractSource(evt.RawJson)
            };

            if (conds.Count > 0)
            {
                try
                {
                    using var doc = JsonDocument.Parse(evt.RawJson);
                    foreach (var c in conds)
                    {
                        var actual = ResolveFieldPath(doc.RootElement, c.FieldPath)?.ToString();
                        row.ConditionResults.Add(new ConditionResult
                        {
                            FieldPath = c.FieldPath,
                            Operator = c.Operator,
                            ExpectedValue = c.Value,
                            ActualValue = actual,
                            Passed = EvaluateCondition(c, doc.RootElement)
                        });
                    }
                    row.Matched = op == "AND"
                        ? row.ConditionResults.All(r => r.Passed)
                        : row.ConditionResults.Any(r => r.Passed);
                }
                catch (JsonException) { row.Matched = false; }
            }

            if (firstMatch == null && row.Matched) firstMatch = row;
            firstEvent ??= row;
            result.Events.Add(row);
        }

        result.MatchedCount = result.Events.Count(e => e.Matched);

        // Render notification preview using the message template
        var basisEvent = firstMatch ?? firstEvent;
        if (basisEvent != null)
        {
            var ruleName = string.IsNullOrWhiteSpace(name) ? "Rule" : name;
            var template = string.IsNullOrWhiteSpace(messageTemplate)
                ? $"[{ruleName}] Event matched"
                : messageTemplate;

            try
            {
                using var doc = JsonDocument.Parse(basisEvent.RawJson);
                result.PreviewTitle = ruleName;
                result.PreviewBody = RenderTemplate(template, ruleName, doc.RootElement);
                result.PreviewBasis = firstMatch != null
                    ? $"Using matched event at {basisEvent.Timestamp.ToLocalTime():HH:mm:ss}"
                    : $"Using most recent event at {basisEvent.Timestamp.ToLocalTime():HH:mm:ss} (not matched by current rule)";
            }
            catch (JsonException) { }
        }

        return Partial("_MatchPreview", result);
    }

    private static string RenderTemplate(string template, string ruleName, JsonElement eventData)
    {
        var result = template;
        result = result.Replace("{ruleName}", ruleName);
        result = result.Replace("{timestamp}", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
        result = result.Replace("{localtime}", DateTime.Now.ToString("M/d/yyyy h:mm:ss tt"));

        var regex = new System.Text.RegularExpressions.Regex(@"\{([^}]+)\}");
        result = regex.Replace(result, match =>
        {
            var fieldPath = match.Groups[1].Value;
            if (fieldPath is "ruleName" or "timestamp" or "localtime") return match.Value;
            var value = ResolveFieldPath(eventData, fieldPath);
            return value?.ToString() ?? match.Value;
        });

        return result;
    }

    private static string ExtractSource(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.TryGetProperty("source", out var s)) return s.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("velocityEventType", out var v)) return v.GetString() ?? "";
        }
        catch { }
        return "Event";
    }

    private static bool EvaluateCondition(ConditionInput condition, JsonElement eventData)
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
            var found = false;
            foreach (var prop in current.EnumerateObject())
            {
                if (string.Equals(prop.Name, segment, StringComparison.OrdinalIgnoreCase))
                {
                    current = prop.Value;
                    found = true;
                    break;
                }
            }
            if (!found) return null;
        }
        return current;
    }

    private static int CompareNumeric(string actual, string expected)
    {
        if (double.TryParse(actual, out var a) && double.TryParse(expected, out var b))
            return a.CompareTo(b);
        return string.Compare(actual, expected, StringComparison.OrdinalIgnoreCase);
    }
}
