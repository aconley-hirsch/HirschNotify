namespace HirschNotify.Services.Health;

/// <summary>
/// A long-running producer of <see cref="HealthEvent"/>s. Implementations self-manage
/// their cadence (poll loop, event subscription, etc.) and should only return from
/// <see cref="RunAsync"/> when the supplied token is cancelled.
/// </summary>
/// <remarks>
/// Register each source as a singleton so <see cref="Workers.VelocitySreHealthWorker"/>
/// can enumerate them through DI. Sources that aren't supported on the current
/// platform / configuration should return <c>false</c> from <see cref="IsEnabled"/>
/// and will be skipped.
/// </remarks>
public interface IHealthSource
{
    /// <summary>Logical name, used for logging and matching config sections.</summary>
    string Name { get; }

    /// <summary>
    /// Whether this source should run given the current platform and settings.
    /// Checked once at worker startup.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Run until the cancellation token is signalled. Implementations must not
    /// throw on <see cref="OperationCanceledException"/> caused by the token;
    /// any other exception will be logged and the source will be restarted.
    /// </summary>
    Task RunAsync(IHealthEventEmitter emitter, CancellationToken cancellationToken);
}
