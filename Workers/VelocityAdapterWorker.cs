using System.Text.Json;
using HirschNotify.Services;
using VelocityAdapter;

namespace HirschNotify.Workers;

public class VelocityAdapterWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConnectionState _connectionState;
    private readonly IEventProcessor _eventProcessor;
    private readonly IVelocityServerAccessor _serverAccessor;
    private readonly ILogger<VelocityAdapterWorker> _logger;
    private VelocityServer? _server;

    public VelocityAdapterWorker(
        IServiceScopeFactory scopeFactory,
        ConnectionState connectionState,
        IEventProcessor eventProcessor,
        IVelocityServerAccessor serverAccessor,
        ILogger<VelocityAdapterWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _connectionState = connectionState;
        _eventProcessor = eventProcessor;
        _serverAccessor = serverAccessor;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(2000, stoppingToken);

            // Check if VelocityAdapter mode is enabled
            using (var scope = _scopeFactory.CreateScope())
            {
                var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
                var mode = await settings.GetAsync("EventSource:Mode") ?? "WebSocket";
                if (mode == "WebSocket")
                {
                    _logger.LogInformation("Event source set to WebSocket — VelocityAdapter worker disabled");
                    return;
                }
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ConnectAndListenAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "VelocityAdapter connection error");
                    _connectionState.Status = "Disconnected";
                    _connectionState.ConnectedSince = null;
                    await Task.Delay(5000, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown
        }
    }

    private async Task ConnectAndListenAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        var config = await VelocityConnectionResolver.ResolveAsync(settings, _logger);
        if (config == null)
        {
            _logger.LogWarning(
                "Velocity connection settings not found. Configure Velocity:SqlServer + Velocity:Database " +
                "in the settings page or install on a machine with the Velocity client registry. Waiting...");
            await Task.Delay(30000, stoppingToken);
            return;
        }

        _connectionState.Status = "Connecting";
        _logger.LogInformation(
            "Connecting to Velocity database {Database} on {Server} (Windows auth, app role: {HasAppRole})",
            config.Database, config.SqlServer, !string.IsNullOrEmpty(config.ApplicationRole));

        _server = new VelocityServer(bEnableLogging: true);
        var connected = new TaskCompletionSource<bool>();

        _server.ConnectionSuccess += () =>
        {
            _logger.LogInformation("Connected to Velocity server");
            _connectionState.Status = "Connected";
            _connectionState.ConnectedSince = DateTime.UtcNow;
            _serverAccessor.Set(_server!);
            connected.TrySetResult(true);
        };

        _server.ConnectionFailure += (string sMessage) =>
        {
            _logger.LogError("Velocity connection failed: {Message}", sMessage);
            _connectionState.Status = "Disconnected";
            _serverAccessor.Clear();
            connected.TrySetResult(false);
        };

        _server.OnDisconnect += (bool forceShutdown) =>
        {
            _logger.LogWarning("Disconnected from Velocity server (forced: {Forced})", forceShutdown);
            _connectionState.Status = "Disconnected";
            _connectionState.ConnectedSince = null;
            _serverAccessor.Clear();
        };

        _server._logMessage += (string msg) =>
        {
            _logger.LogDebug("VelocityAdapter: {Message}", msg);
        };

        // Subscribe to events
        _server.ExternalEvent += (evt) => OnExternalEvent(evt);
        _server.InternalEvent += (evt) => OnInternalEvent(evt);
        _server.SystemTransaction += (evt) => OnTransactionEvent(evt);
        _server.SoftwareEvent += (evt) => OnSoftwareEvent(evt);
        _server.MiscEvent += (evt) => OnMiscEvent(evt);
        _server.AlarmActive += (alarm) => OnAlarmActive(alarm);
        _server.AlarmAcknowledged += (alarmId, timestamp, op, workstation, ackType) =>
            OnAlarmAcknowledged(alarmId, timestamp, op, workstation);
        _server.AlarmCleared += (alarmId, timestamp, op, workstation, clrType) =>
            OnAlarmCleared(alarmId, timestamp, op, workstation);

        // Pick the right Connect overload based on the resolved auth mode
        // (Windows / Windows + AppRole / SQL mixed-mode).
        VelocityConnectionResolver.ApplyConnect(_server, config);

        // Wait for connection result with timeout
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(15, config.ConnectionTimeoutSec * 2)));

        try
        {
            var success = await connected.Task.WaitAsync(cts.Token);
            if (!success)
            {
                _connectionState.Status = "Disconnected";
                await Task.Delay(10000, stoppingToken);
                return;
            }
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogError("Velocity connection timed out");
            _connectionState.Status = "Disconnected";
            return;
        }

        // Stay alive until disconnected or cancelled
        try
        {
            while (!stoppingToken.IsCancellationRequested && _server.IsConnected)
            {
                await Task.Delay(1000, stoppingToken);
            }
        }
        finally
        {
            _serverAccessor.Clear();
            if (_server.IsConnected)
            {
                _server.Disconnect();
            }
            _server = null;
        }
    }

    private void OnExternalEvent(IExternalEvent evt)
    {
        var json = SerializeEvent(new
        {
            source = "ExternalEvent",
            eventID = evt.EventID,
            eventType = evt.EventType.ToString(),
            description = evt.Description,
            address = evt.Address,
            controllerID = evt.ControllerID,
            controllerTime = evt.ControllerTime,
            serverTime = evt.ServerTime,
            alarmLevelPriority = evt.AlarmLevelPriority,
            reportAsAlarm = evt.ReportAsAlarm,
            fromState = evt.FromState,
            toState = evt.ToState,
            portAddress = evt.PortAddress,
            serverID = evt.ServerID,
            velocityEventType = evt.VelocityEventType
        });

        _connectionState.IncrementEvents();
        _logger.LogInformation("External event: {Description} (ID: {EventID})", evt.Description, evt.EventID);
        _ = _eventProcessor.ProcessEventAsync(json);
    }

    private void OnInternalEvent(IInternalEvent evt)
    {
        var json = SerializeEvent(new
        {
            source = "InternalEvent",
            eventID = evt.EventID,
            eventType = evt.EventType.ToString(),
            description = evt.Description,
            address = evt.Address,
            controllerID = evt.ControllerID,
            controllerTime = evt.ControllerTime,
            serverTime = evt.ServerTime,
            alarmLevelPriority = evt.AlarmLevelPriority,
            reportAsAlarm = evt.ReportAsAlarm,
            fromState = evt.FromState,
            portAddress = evt.PortAddress,
            serverID = evt.ServerID,
            velocityEventType = evt.VelocityEventType
        });

        _connectionState.IncrementEvents();
        _logger.LogInformation("Internal event: {Description} (ID: {EventID})", evt.Description, evt.EventID);
        _ = _eventProcessor.ProcessEventAsync(json);
    }

    private void OnTransactionEvent(ITransactionEvent evt)
    {
        var json = SerializeEvent(new
        {
            source = "TransactionEvent",
            eventID = evt.EventID,
            eventType = evt.EventType,
            description = evt.Description,
            address = evt.Address,
            controllerID = evt.ControllerID,
            controllerTime = evt.ControllerTime,
            serverTime = evt.ServerTime,
            alarmLevelPriority = evt.AlarmLevelPriority,
            reportAsAlarm = evt.ReportAsAlarm,
            card = evt.CARD,
            pin = evt.PIN,
            disposition = evt.Disposition.ToString(),
            transactionType = evt.TransactionType.ToString(),
            uid1 = evt.UID1,
            uid2 = evt.UID2,
            portAddress = evt.PortAddress,
            serverID = evt.ServerID,
            velocityEventType = evt.VelocityEventType
        });

        _connectionState.IncrementEvents();
        _logger.LogInformation("Transaction event: {Description} (ID: {EventID})", evt.Description, evt.EventID);
        _ = _eventProcessor.ProcessEventAsync(json);
    }

    private void OnSoftwareEvent(ISoftwareEvent evt)
    {
        var json = SerializeEvent(new
        {
            source = "SoftwareEvent",
            eventID = evt.EventID,
            eventType = evt.EventType,
            description = evt.Description,
            address = evt.Address,
            serverTime = evt.ServerTime,
            timestamp = evt.TimeStamp,
            sourceID = evt.SourceID,
            sourceName = evt.SourceName,
            userID = evt.UserID,
            userName = evt.UserName,
            velocityEventType = "SoftwareEvent"
        });

        _connectionState.IncrementEvents();
        _logger.LogInformation("Software event: {Description} (ID: {EventID})", evt.Description, evt.EventID);
        _ = _eventProcessor.ProcessEventAsync(json);
    }

    private void OnMiscEvent(IMiscEvent evt)
    {
        var json = SerializeEvent(new
        {
            source = "MiscEvent",
            eventID = evt.EventID,
            eventType = evt.EventType.ToString(),
            description = evt.Description,
            address = evt.Address,
            controllerTime = evt.ControllerTime,
            serverTime = evt.ServerTime,
            alarmLevelPriority = evt.AlarmLevelPriority,
            reportAsAlarm = evt.ReportAsAlarm,
            portAddress = evt.PortAddress,
            serverID = evt.ServerID,
            velocityEventType = "MiscEvent"
        });

        _connectionState.IncrementEvents();
        _logger.LogInformation("Misc event: {Description} (ID: {EventID})", evt.Description, evt.EventID);
        _ = _eventProcessor.ProcessEventAsync(json);
    }

    private void OnAlarmActive(IAlarmActive alarm)
    {
        var json = SerializeEvent(new
        {
            source = "AlarmActive",
            eventID = alarm.EventId,
            eventType = alarm.EventType,
            description = alarm.Description,
            alarmId = alarm.AlarmId,
            serverTime = alarm.ServerTime,
            controllerTime = alarm.DeviceTime,
            domainId = alarm.DomainId,
            serverId = alarm.ServerId,
            portAddress = alarm.PortAddress,
            velocityEventType = "AlarmActive"
        });

        _connectionState.IncrementEvents();
        _logger.LogInformation("Alarm active: {Description} (ID: {EventID})", alarm.Description, alarm.EventId);
        _ = _eventProcessor.ProcessEventAsync(json);
    }

    private void OnAlarmAcknowledged(int alarmId, DateTime timestamp, string operatorName, string workstation)
    {
        var json = SerializeEvent(new
        {
            source = "AlarmAcknowledged",
            alarmId,
            timestamp,
            operatorName,
            workstation,
            velocityEventType = "AlarmAcknowledged"
        });

        _connectionState.IncrementEvents();
        _logger.LogDebug("Alarm {AlarmId} acknowledged by {Operator}", alarmId, operatorName);
        _ = _eventProcessor.ProcessEventAsync(json);
    }

    private void OnAlarmCleared(int alarmId, DateTime timestamp, string operatorName, string workstation)
    {
        var json = SerializeEvent(new
        {
            source = "AlarmCleared",
            alarmId,
            timestamp,
            operatorName,
            workstation,
            velocityEventType = "AlarmCleared"
        });

        _connectionState.IncrementEvents();
        _logger.LogDebug("Alarm {AlarmId} cleared by {Operator}", alarmId, operatorName);
        _ = _eventProcessor.ProcessEventAsync(json);
    }

    private static string SerializeEvent(object eventData)
    {
        return JsonSerializer.Serialize(eventData, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        });
    }
}
