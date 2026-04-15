namespace HirschNotify.Services;

/// <summary>
/// Cross-platform fallback for <see cref="IServiceAccountManager"/>. Registered
/// on non-Windows hosts so dependency injection can resolve the service; every
/// operation throws <see cref="PlatformNotSupportedException"/>. The Settings
/// page checks <see cref="OperatingSystem.IsWindows"/> before rendering the
/// account-change form, so this should only get exercised by accidental POSTs
/// in a dev environment.
/// </summary>
public sealed class UnsupportedServiceAccountManager : IServiceAccountManager
{
    public Task<AccountValidationResult> ValidateAsync(string username, string password, CancellationToken ct) =>
        Task.FromResult(new AccountValidationResult(false, "Service account management is only supported on Windows."));

    public Task ApplyAsync(string username, string password, CancellationToken ct) =>
        throw new PlatformNotSupportedException("Service account management is only supported on Windows.");
}
