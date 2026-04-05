using System.Text.Json;
using EventAlertService.Models;

namespace EventAlertService.Services;

public interface IFilterEngine
{
    Task<List<FilterRule>> EvaluateAsync(JsonElement eventData);
}
