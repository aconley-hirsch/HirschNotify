using System.Text.Json;

namespace HirschNotify.Services;

public interface IEventProcessor
{
    Task ProcessEventAsync(string jsonMessage, CancellationToken stoppingToken = default);
    Task ProcessEventAsync(JsonElement eventData, string rawJson, CancellationToken stoppingToken = default);
}
