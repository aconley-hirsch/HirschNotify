using System.Runtime.Versioning;
using System.ServiceProcess;

namespace HirschNotify.Services.Health;

[SupportedOSPlatform("windows")]
public sealed class WindowsServicesInspector : IWindowsServicesInspector
{
    public bool IsSupported => true;

    public IReadOnlyList<InstalledWindowsService> List()
    {
        ServiceController[] services;
        try
        {
            services = ServiceController.GetServices();
        }
        catch
        {
            return Array.Empty<InstalledWindowsService>();
        }

        try
        {
            var result = new List<InstalledWindowsService>(services.Length);
            foreach (var s in services)
            {
                string name, displayName, status, startMode;
                try
                {
                    name = s.ServiceName;
                    displayName = s.DisplayName;
                    status = s.Status.ToString();
                    startMode = s.StartType.ToString();
                }
                catch
                {
                    // Individual services can throw when the caller lacks
                    // permission to query their state — skip those rather
                    // than failing the whole list.
                    continue;
                }
                result.Add(new InstalledWindowsService(name, displayName, status, startMode));
            }
            result.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
            return result;
        }
        finally
        {
            foreach (var s in services) s.Dispose();
        }
    }
}

public sealed class UnsupportedWindowsServicesInspector : IWindowsServicesInspector
{
    public bool IsSupported => false;

    public IReadOnlyList<InstalledWindowsService> List() => Array.Empty<InstalledWindowsService>();
}
