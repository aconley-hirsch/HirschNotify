namespace HirschNotify.Services;

/// <summary>
/// Surface for changing the Windows service account at runtime from inside
/// the running HirschNotify service. On non-Windows hosts the registered
/// implementation throws <see cref="PlatformNotSupportedException"/>.
/// </summary>
public interface IServiceAccountManager
{
    /// <summary>
    /// Validate the supplied credentials against Windows by attempting a
    /// service-logon with <c>LogonUser</c>. Returns a result rather than
    /// throwing so the caller can surface a friendly error to the UI.
    /// </summary>
    Task<AccountValidationResult> ValidateAsync(string username, string password, CancellationToken ct);

    /// <summary>
    /// Apply the new account to the <c>HirschNotify</c> service:
    ///   1. Grant <c>SeServiceLogonRight</c> to the account via LSA.
    ///   2. Grant filesystem ACLs on the install directory (read/execute),
    ///      <c>Logs</c>, <c>Data</c>, and <c>Keys</c> (modify).
    ///   3. Call <c>ChangeServiceConfig</c> to update the SCM record.
    ///   4. Spawn a detached helper process that stops and restarts the
    ///      service, with a <c>LocalSystem</c> rollback branch if the new
    ///      account fails to start.
    ///
    /// The current process does not wait for the restart — it returns as
    /// soon as the helper is spawned, then the ASP.NET host's shutdown hook
    /// takes over as the helper issues <c>net stop</c>.
    /// </summary>
    /// <remarks>
    /// The caller must NOT persist <paramref name="password"/> anywhere —
    /// it's passed directly to <c>ChangeServiceConfig</c> and discarded.
    /// </remarks>
    Task ApplyAsync(string username, string password, CancellationToken ct);
}

public sealed record AccountValidationResult(bool IsValid, string? ErrorMessage);
