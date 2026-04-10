using System.Runtime.InteropServices;
using Microsoft.Win32;
using VelocityAdapter;

namespace HirschNotify.Services;

/// <summary>
/// Fully resolved settings needed to open a <see cref="VelocityServer"/> connection.
/// Built by <see cref="VelocityConnectionResolver.ResolveAsync"/> from a merge of
/// the Velocity client registry (when present) and the user's settings overrides.
/// </summary>
/// <remarks>
/// Velocity is always connected to with Windows integrated auth — the service
/// account's token is presented to SQL Server, and the encrypted application
/// role is supplied so the SDK can elevate to the permissions Velocity expects.
/// The service account ALSO needs to be configured as a Velocity Operator inside
/// Velocity itself; that is the deployment requirement, not a setting we can
/// drive from this app.
/// </remarks>
public sealed record VelocityConnectionConfig
{
    public required string SqlServer { get; init; }
    public required string Database { get; init; }

    /// <summary>
    /// Velocity application role string. The Velocity client registry stores this
    /// as an encrypted blob, and the SDK's <c>ConnectDecrypt</c> method decrypts
    /// it at connect time. We pass it through unchanged.
    /// </summary>
    public string? ApplicationRole { get; init; }

    public int ConnectionTimeoutSec { get; init; } = 15;
    public int CommandTimeoutSec { get; init; } = 0;

    /// <summary>True when at least one field came from a settings override rather than the registry.</summary>
    public bool HasOverrides { get; init; }
}

/// <summary>
/// Builds a <see cref="VelocityConnectionConfig"/> from the registry plus user
/// settings, and applies it to a <see cref="VelocityServer"/> instance using
/// <c>ConnectDecrypt</c> (Windows auth + encrypted application role). Lives in
/// one place so the background worker and the "Test Connection" handler stay in
/// sync.
/// </summary>
public static class VelocityConnectionResolver
{
    private const string RegistryKeyPath = @"SOFTWARE\Wow6432Node\Hirsch Electronics\Velocity\Client";

    public static async Task<VelocityConnectionConfig?> ResolveAsync(
        ISettingsService settings,
        ILogger? logger = null)
    {
        var (regSqlServer, regDatabase, regAppRole) = ReadRegistry(logger);

        var sqlServer = await settings.GetAsync("Velocity:SqlServer");
        var database = await settings.GetAsync("Velocity:Database");
        var appRole = await settings.GetEncryptedAsync("Velocity:AppRole");
        var connectTimeoutText = await settings.GetAsync("Velocity:ConnectionTimeoutSec");
        var commandTimeoutText = await settings.GetAsync("Velocity:CommandTimeoutSec");

        var hasOverrides =
            !string.IsNullOrEmpty(sqlServer) ||
            !string.IsNullOrEmpty(database) ||
            !string.IsNullOrEmpty(appRole);

        // Settings take precedence; fall back to registry for any field left blank.
        sqlServer = !string.IsNullOrEmpty(sqlServer) ? sqlServer : regSqlServer;
        database = !string.IsNullOrEmpty(database) ? database : regDatabase;
        appRole = !string.IsNullOrEmpty(appRole) ? appRole : regAppRole;

        if (string.IsNullOrEmpty(sqlServer) || string.IsNullOrEmpty(database))
            return null;

        return new VelocityConnectionConfig
        {
            SqlServer = sqlServer!,
            Database = database!,
            ApplicationRole = appRole,
            ConnectionTimeoutSec = ParseInt(connectTimeoutText, 15),
            CommandTimeoutSec = ParseInt(commandTimeoutText, 0),
            HasOverrides = hasOverrides,
        };
    }

    /// <summary>
    /// Apply the resolved config to a freshly-constructed <see cref="VelocityServer"/>.
    /// Caller is responsible for wiring up <c>ConnectionSuccess</c>/<c>ConnectionFailure</c>
    /// handlers BEFORE calling this method, since some Connect overloads fire events
    /// synchronously.
    /// </summary>
    public static void ApplyConnect(VelocityServer server, VelocityConnectionConfig config)
    {
        if (config.CommandTimeoutSec > 0)
            server.setSQLTimeout(config.CommandTimeoutSec);

        if (!string.IsNullOrEmpty(config.ApplicationRole))
        {
            // Standard Velocity deployment: Windows auth + decrypted application role.
            // The service account must also be configured as a Velocity Operator.
            server.ConnectDecrypt(config.SqlServer, config.Database, config.ApplicationRole);
        }
        else
        {
            // Fallback: Windows auth without an application role. Only works on
            // installs where Velocity isn't enforcing the role (rare).
            server.Connect(config.SqlServer, config.Database);
        }
    }

    private static (string? SqlServer, string? Database, string? AppRole) ReadRegistry(ILogger? logger)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return (null, null, null);

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistryKeyPath);
            if (key == null)
                return (null, null, null);

            return (
                key.GetValue("SQL Server") as string,
                key.GetValue("Database") as string,
                key.GetValue("ApplicationRole") as string);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to read Velocity registry settings");
            return (null, null, null);
        }
    }

    private static int ParseInt(string? text, int fallback) =>
        int.TryParse(text, out var value) && value > 0 ? value : fallback;
}
