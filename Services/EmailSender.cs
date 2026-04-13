using System.Net;
using System.Net.Mail;
using System.Text.Json;
using HirschNotify.Models;

namespace HirschNotify.Services;

public class EmailSender : IContactMethodSender
{
    public string Type => "email";
    public string DisplayName => "Email (SMTP)";
    public string Description => "Send alert notifications via email using an SMTP server.";
    public string IconSvg => """<svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect width="20" height="16" x="2" y="4" rx="2"/><path d="m22 7-8.97 5.7a1.94 1.94 0 0 1-2.06 0L2 7"/></svg>""";

    public ContactMethodField[] ConfigurationFields =>
    [
        new("Host", "SMTP Host", "text", "smtp.gmail.com", "SMTP server hostname."),
        new("Port", "Port", "number", "587", "Default: 587 (STARTTLS)."),
        new("Username", "Username", "text", "user@example.com"),
        new("Password", "Password", "password", null, "Leave blank to keep current password.", IsSecret: true),
        new("FromAddress", "From Address", "email", "noreply@hirschnotify.local", "The sender address for outgoing emails."),
        new("EnableSsl", "Enable SSL/TLS", "select", null, "Encrypt the SMTP connection."),
    ];

    private readonly ISettingsService _settings;
    private readonly ILogger<EmailSender> _logger;

    public EmailSender(ISettingsService settings, ILogger<EmailSender> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public async Task<bool> SendAsync(ContactMethod method, string subject, string body)
    {
        try
        {
            var config = JsonSerializer.Deserialize<EmailConfig>(method.Configuration);
            if (config == null || string.IsNullOrWhiteSpace(config.Address))
            {
                _logger.LogWarning("Invalid email configuration for ContactMethod {Id}", method.Id);
                return false;
            }

            var host = await _settings.GetAsync("ContactMethod:email:Host");
            if (string.IsNullOrEmpty(host))
            {
                _logger.LogWarning("SMTP not configured, skipping email for ContactMethod {Id}", method.Id);
                return false;
            }

            var portStr = await _settings.GetAsync("ContactMethod:email:Port");
            var username = await _settings.GetAsync("ContactMethod:email:Username");
            var password = await _settings.GetEncryptedAsync("ContactMethod:email:Password");
            var fromAddress = await _settings.GetAsync("ContactMethod:email:FromAddress") ?? "noreply@hirschnotify.local";
            var enableSslStr = await _settings.GetAsync("ContactMethod:email:EnableSsl");

            var port = int.TryParse(portStr, out var p) ? p : 587;
            var enableSsl = enableSslStr != "false";

            using var client = new SmtpClient(host, port)
            {
                EnableSsl = enableSsl,
                DeliveryMethod = SmtpDeliveryMethod.Network
            };

            if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
            {
                client.Credentials = new NetworkCredential(username, password);
            }

            using var message = new MailMessage(fromAddress, config.Address, subject, body);
            await client.SendMailAsync(message);

            _logger.LogInformation("Email sent to {Address} for ContactMethod {Id}", config.Address, method.Id);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email for ContactMethod {Id}", method.Id);
            return false;
        }
    }

    public string? ValidateConfiguration(string configurationJson)
    {
        try
        {
            var config = JsonSerializer.Deserialize<EmailConfig>(configurationJson);
            if (config == null || string.IsNullOrWhiteSpace(config.Address))
                return "Email address is required.";
            if (!MailAddress.TryCreate(config.Address, out _))
                return "Invalid email address format.";
            return null;
        }
        catch
        {
            return "Invalid configuration format.";
        }
    }

    private class EmailConfig
    {
        public string Address { get; set; } = string.Empty;
    }
}
