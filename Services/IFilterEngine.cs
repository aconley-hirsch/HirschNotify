using System.Text.Json;
using HirschNotify.Models;

namespace HirschNotify.Services;

public interface IFilterEngine
{
    Task<List<FilterRule>> EvaluateAsync(JsonElement eventData);
}
