namespace HirschNotify.Services.Health;

public interface IWindowsServicesInspector
{
    bool IsSupported { get; }

    IReadOnlyList<InstalledWindowsService> List();
}

public sealed record InstalledWindowsService(
    string Name,
    string DisplayName,
    string Status,
    string StartMode);
