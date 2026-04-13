using HirschNotify.Models;

namespace HirschNotify.Services;

public record ContactMethodField(
    string Key,
    string Label,
    string Type,
    string? Placeholder = null,
    string? HelpText = null,
    bool IsSecret = false);

public interface IContactMethodSender
{
    string Type { get; }
    string DisplayName { get; }
    string Description { get; }
    string IconSvg { get; }
    ContactMethodField[] ConfigurationFields { get; }

    Task<bool> SendAsync(ContactMethod method, string subject, string body);
    string? ValidateConfiguration(string configurationJson);
}
