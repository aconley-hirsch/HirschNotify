using System.Collections.Concurrent;

namespace EventAlertService.Services;

public class RecentEvent
{
    public DateTime Timestamp { get; set; }
    public string RawJson { get; set; } = "";
    public string? Preview { get; set; }
    public List<string> MatchedRules { get; set; } = new();
    public bool AlertSent { get; set; }
}

public class ConnectionState
{
    private volatile string _status = "Disconnected";
    private DateTime? _connectedSince;
    private long _eventsReceived;
    private long _alertsSentToday;
    private DateTime _alertsResetDate = DateTime.UtcNow.Date;

    private readonly ConcurrentQueue<RecentEvent> _recentEvents = new();
    private const int MaxRecentEvents = 100;

    public string Status
    {
        get => _status;
        set => _status = value;
    }

    public DateTime? ConnectedSince
    {
        get => _connectedSince;
        set => _connectedSince = value;
    }

    public long EventsReceived => Interlocked.Read(ref _eventsReceived);
    public long AlertsSentToday
    {
        get
        {
            ResetIfNewDay();
            return Interlocked.Read(ref _alertsSentToday);
        }
    }

    public void IncrementEvents() => Interlocked.Increment(ref _eventsReceived);

    public void IncrementAlerts()
    {
        ResetIfNewDay();
        Interlocked.Increment(ref _alertsSentToday);
    }

    public void AddRecentEvent(RecentEvent evt)
    {
        _recentEvents.Enqueue(evt);
        while (_recentEvents.Count > MaxRecentEvents)
            _recentEvents.TryDequeue(out _);
    }

    public List<RecentEvent> GetRecentEvents(int count = 50)
    {
        return _recentEvents.Reverse().Take(count).ToList();
    }

    private void ResetIfNewDay()
    {
        var today = DateTime.UtcNow.Date;
        if (_alertsResetDate < today)
        {
            Interlocked.Exchange(ref _alertsSentToday, 0);
            _alertsResetDate = today;
        }
    }
}
