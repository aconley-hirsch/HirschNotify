using System.Text.Encodings.Web;
using EventAlertService.Data;
using EventAlertService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace EventAlertService.Pages;

[Authorize]
public class IndexModel : PageModel
{
    private readonly ConnectionState _connectionState;
    private readonly AppDbContext _db;
    private readonly ISettingsService _settings;

    public IndexModel(ConnectionState connectionState, AppDbContext db, ISettingsService settings)
    {
        _connectionState = connectionState;
        _db = db;
        _settings = settings;
    }

    public void OnGet() { }

    public async Task<IActionResult> OnGetStatusAsync()
    {
        var status = _connectionState.Status;
        var connectedSince = _connectionState.ConnectedSince;
        var eventsReceived = _connectionState.EventsReceived;
        var alertsToday = _connectionState.AlertsSentToday;

        var statusClass = status switch
        {
            "Connected" => "status-connected",
            "Disconnected" or "Auth Failed" => "status-disconnected",
            _ => "status-reconnecting"
        };

        var relayRegistered = await _settings.GetAsync("Relay:Registered") == "true";
        var relayUrl = await _settings.GetAsync("Relay:Url") ?? "";
        var relayName = await _settings.GetAsync("Relay:InstanceName") ?? "";
        var eventSourceMode = await _settings.GetAsync("EventSource:Mode") ?? "WebSocket";
        var sourceLabel = eventSourceMode == "VelocityAdapter" ? "Velocity Connection" : "WebSocket";

        var html = $@"
        <div class='stats-grid stats-grid-3'>
            <div class='stat-card health-card'>
                <span class='label'>Health</span>
                <div class='health-row'>
                    <span class='health-label'>{sourceLabel}</span>
                    <span class='status-badge {statusClass}'>{status}</span>
                </div>
                <div class='health-row'>
                    <span class='health-label'>Push Relay</span>
                    <span class='status-badge {(relayRegistered ? "status-connected" : "status-disconnected")}'>{(relayRegistered ? "Connected" : "Not Registered")}</span>
                </div>
                {(connectedSince.HasValue ? $"<small>{sourceLabel} since {connectedSince.Value.ToLocalTime():MMM d, h:mm tt}</small>" : "")}
                {(relayRegistered ? $"<small>Relay: {HtmlEncoder.Default.Encode(relayName)}</small>" : "")}
            </div>
            <div class='stat-card'>
                <span class='label'>Events Received</span>
                <span class='value'>{eventsReceived:N0}</span>
            </div>
            <div class='stat-card'>
                <span class='label'>Alerts Sent Today</span>
                <span class='value'>{alertsToday:N0}</span>
            </div>
        </div>";

        return Content(html, "text/html");
    }

    public async Task<IActionResult> OnGetRecipientsAsync()
    {
        var recipients = await _db.Recipients.OrderByDescending(r => r.CreatedAt).Take(5).ToListAsync();

        if (recipients.Count == 0)
        {
            return Content("<p class='text-muted-msg'>No recipients yet. <a href='/Recipients/Edit'>Add one</a>.</p>", "text/html");
        }

        var html = new System.Text.StringBuilder();
        foreach (var r in recipients)
        {
            var statusClass = r.IsActive ? "status-connected" : "status-disconnected";
            var statusText = r.IsActive ? "Active" : "Inactive";
            var name = HtmlEncoder.Default.Encode(r.Name);

            html.Append($@"
            <a href='/Recipients/Edit?id={r.Id}' class='dashboard-list-item'>
                <div>
                    <span class='dashboard-list-name'>{name}</span>
                </div>
                <span class='status-badge {statusClass}'>{statusText}</span>
            </a>");
        }

        return Content(html.ToString(), "text/html");
    }

    public IActionResult OnGetEvents()
    {
        var events = _connectionState.GetRecentEvents(20);

        if (events.Count == 0)
        {
            return Content("<p class='text-muted-msg'>No events received yet. Waiting for event source connection...</p>", "text/html");
        }

        var html = new System.Text.StringBuilder();

        foreach (var evt in events)
        {
            var preview = HtmlEncoder.Default.Encode(evt.Preview ?? "");
            var time = evt.Timestamp.ToLocalTime().ToString("h:mm:ss tt");
            var hasMatch = evt.MatchedRules.Count > 0;
            var rules = hasMatch ? HtmlEncoder.Default.Encode(string.Join(", ", evt.MatchedRules)) : "";

            var alertLabel = evt.AlertSent ? "<mark>Sent</mark>"
                : hasMatch ? "<mark class='mark-danger'>Throttled</mark>"
                : "";

            html.Append($@"
            <div class='event-row {(hasMatch ? "event-matched" : "")}'>
                <span class='event-time'>{time}</span>
                <span class='event-preview'>{preview}</span>
                <span class='event-rules'>{(hasMatch ? rules : "")}</span>
                <span class='event-alert'>{alertLabel}</span>
            </div>");
        }

        return Content(html.ToString(), "text/html");
    }
}
