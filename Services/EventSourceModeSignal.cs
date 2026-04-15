namespace HirschNotify.Services;

// Bumped by SettingsModel whenever EventSource:Mode changes, so
// WebSocketWorker and VelocityAdapterWorker can hand off control
// without a service restart. Each worker links its connect/listen
// cancellation to Token and re-checks the mode on the next pass.
public class EventSourceModeSignal
{
    private CancellationTokenSource _cts = new();

    public CancellationToken Token => _cts.Token;

    public void Trigger()
    {
        var old = Interlocked.Exchange(ref _cts, new CancellationTokenSource());
        old.Cancel();
        old.Dispose();
    }
}
