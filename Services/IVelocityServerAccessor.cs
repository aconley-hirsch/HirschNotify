using VelocityAdapter;

namespace HirschNotify.Services;

/// <summary>
/// Holds a reference to the currently-connected <see cref="VelocityServer"/> so
/// other services (notably health sources) can read SDK state without owning
/// the connection lifecycle themselves. <see cref="Workers.VelocityAdapterWorker"/>
/// is the single publisher — it sets the reference on successful connect and
/// clears it on disconnect.
/// </summary>
public interface IVelocityServerAccessor
{
    /// <summary>
    /// Current VelocityServer instance, or <c>null</c> if no connection is active.
    /// Callers should also check <see cref="VelocityServer.IsConnected"/> before
    /// making SDK calls, as the instance can be stale between a disconnect event
    /// and the worker's cleanup.
    /// </summary>
    VelocityServer? Current { get; }

    void Set(VelocityServer server);
    void Clear();
}

public sealed class VelocityServerAccessor : IVelocityServerAccessor
{
    private VelocityServer? _current;

    public VelocityServer? Current => _current;

    public void Set(VelocityServer server) => _current = server;

    public void Clear() => _current = null;
}
