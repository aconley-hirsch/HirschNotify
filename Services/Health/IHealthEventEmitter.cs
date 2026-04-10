namespace HirschNotify.Services.Health;

/// <summary>
/// Single choke point between <see cref="IHealthSource"/> implementations and the
/// rest of the notification pipeline. Sources should never call
/// <see cref="IEventProcessor"/> directly — go through the emitter so the envelope
/// shape stays consistent.
/// </summary>
public interface IHealthEventEmitter
{
    Task EmitAsync(HealthEvent evt, CancellationToken cancellationToken = default);
}
