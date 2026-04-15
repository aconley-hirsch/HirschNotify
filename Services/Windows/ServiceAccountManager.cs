using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using static HirschNotify.Services.Windows.ServiceAccountPInvoke;

namespace HirschNotify.Services.Windows;

/// <summary>
/// Windows implementation of <see cref="IServiceAccountManager"/>. Walks the
/// four-step dance required to safely move a running Windows service onto a
/// new logon account: validate → grant SeServiceLogonRight → grant filesystem
/// ACLs → <c>ChangeServiceConfig</c> → detached restart with rollback.
/// </summary>
/// <remarks>
/// This class is only registered on Windows (see <c>Program.cs</c>). It
/// assumes the HirschNotify service is running under an account that holds
/// <c>SC_MANAGER_CONNECT</c> and <c>SERVICE_CHANGE_CONFIG</c> on itself
/// (which <c>LocalSystem</c> and any account in the local Administrators
/// group do by default).
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ServiceAccountManager : IServiceAccountManager
{
    private const string ServiceName = "HirschNotify";
    private const string EventLogSource = "HirschNotify";

    private readonly ILogger<ServiceAccountManager> _logger;

    public ServiceAccountManager(ILogger<ServiceAccountManager> logger)
    {
        _logger = logger;
    }

    public Task<AccountValidationResult> ValidateAsync(string username, string password, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(username))
            return Task.FromResult(new AccountValidationResult(false, "Username is required."));
        if (string.IsNullOrEmpty(password))
            return Task.FromResult(new AccountValidationResult(false, "Password is required."));

        var (domain, user) = SplitAccount(username);

        if (!LogonUser(user, domain, password, LOGON32_LOGON_SERVICE, LOGON32_PROVIDER_DEFAULT, out var token))
        {
            var win32 = new Win32Exception(Marshal.GetLastWin32Error());
            return Task.FromResult(new AccountValidationResult(false, $"Credential validation failed: {win32.Message}"));
        }
        CloseHandle(token);
        return Task.FromResult(new AccountValidationResult(true, null));
    }

    public async Task ApplyAsync(string username, string password, CancellationToken ct)
    {
        WriteEventLog($"Applying service account change to '{username}' by in-process request.", EventLogEntryType.Information);

        // 1. Resolve the account SID and grant SeServiceLogonRight via LSA.
        //    Without this, ChangeServiceConfig succeeds but the next `sc start`
        //    fails with error 1069 ("The service did not start due to a logon
        //    failure").
        var sid = LookupSid(username)
            ?? throw new InvalidOperationException($"Unable to resolve SID for account '{username}'.");
        GrantServiceLogonRight(sid);

        // 2. Grant filesystem ACLs on the install directory subtree so the new
        //    account can read binaries and write logs / data / key rings.
        GrantInstallDirAcls(username);

        // 3. Update the service record in SCM.
        ChangeServiceStartName(username, password);
        WriteEventLog($"ChangeServiceConfig succeeded: {ServiceName} → {username}.", EventLogEntryType.Information);

        // 4. Spawn a detached cmd.exe that waits a beat, stops the service,
        //    starts it under the new account, and rolls back to LocalSystem
        //    if the new account fails to start. The helper runs outside the
        //    service process so `net stop` can cleanly tear us down without
        //    a self-deadlock.
        SpawnDetachedRestartHelper();
        await Task.CompletedTask;
    }

    // ── private helpers ───────────────────────────────────────────────

    private static (string? Domain, string User) SplitAccount(string account)
    {
        var trimmed = account.Trim();
        var i = trimmed.IndexOf('\\');
        if (i < 0) return (null, trimmed);
        return (trimmed[..i], trimmed[(i + 1)..]);
    }

    private static byte[]? LookupSid(string accountName)
    {
        uint sidSize = 0;
        uint domainSize = 0;
        LookupAccountName(null, accountName, null, ref sidSize, null, ref domainSize, out _);
        if (sidSize == 0) return null;

        var sid = new byte[sidSize];
        var domain = new StringBuilder((int)domainSize);
        if (!LookupAccountName(null, accountName, sid, ref sidSize, domain, ref domainSize, out _))
            return null;
        return sid;
    }

    private static void GrantServiceLogonRight(byte[] sid)
    {
        var attrs = new LSA_OBJECT_ATTRIBUTES { Length = Marshal.SizeOf<LSA_OBJECT_ATTRIBUTES>() };
        var status = LsaOpenPolicy(IntPtr.Zero, ref attrs, POLICY_CREATE_ACCOUNT | POLICY_LOOKUP_NAMES, out var policy);
        if (status != 0)
            throw new Win32Exception(LsaNtStatusToWinError(status), "LsaOpenPolicy failed.");

        try
        {
            const string RightName = "SeServiceLogonRight";
            var rightBytes = Encoding.Unicode.GetBytes(RightName);
            var rightPtr = Marshal.AllocHGlobal(rightBytes.Length);
            try
            {
                Marshal.Copy(rightBytes, 0, rightPtr, rightBytes.Length);
                var rights = new[]
                {
                    new LSA_UNICODE_STRING
                    {
                        Length = (ushort)rightBytes.Length,
                        MaximumLength = (ushort)rightBytes.Length,
                        Buffer = rightPtr,
                    },
                };
                status = LsaAddAccountRights(policy, sid, rights, 1);
                if (status != 0)
                    throw new Win32Exception(LsaNtStatusToWinError(status), "LsaAddAccountRights failed.");
            }
            finally
            {
                Marshal.FreeHGlobal(rightPtr);
            }
        }
        finally
        {
            LsaClose(policy);
        }
    }

    private void GrantInstallDirAcls(string accountName)
    {
        var installDir = AppContext.BaseDirectory;
        var identity = new NTAccount(accountName);

        Grant(installDir, identity, FileSystemRights.ReadAndExecute);
        foreach (var sub in new[] { "Logs", "Data", "Keys" })
        {
            var path = Path.Combine(installDir, sub);
            if (Directory.Exists(path))
                Grant(path, identity, FileSystemRights.Modify);
        }
    }

    private void Grant(string path, NTAccount identity, FileSystemRights rights)
    {
        try
        {
            var info = new DirectoryInfo(path);
            var acl = info.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(
                identity,
                rights,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            info.SetAccessControl(acl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to grant {Rights} on {Path} to {Account}", rights, path, identity.Value);
        }
    }

    private static void ChangeServiceStartName(string username, string password)
    {
        var scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenSCManager failed.");

        try
        {
            var svc = OpenService(scm, ServiceName, SERVICE_CHANGE_CONFIG);
            if (svc == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenService failed.");

            try
            {
                var ok = ChangeServiceConfig(
                    svc,
                    SERVICE_NO_CHANGE,
                    SERVICE_NO_CHANGE,
                    SERVICE_NO_CHANGE,
                    lpBinaryPathName: null,
                    lpLoadOrderGroup: null,
                    lpdwTagId: IntPtr.Zero,
                    lpDependencies: null,
                    lpServiceStartName: username,
                    lpPassword: password,
                    lpDisplayName: null);
                if (!ok)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "ChangeServiceConfig failed.");
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    private void SpawnDetachedRestartHelper()
    {
        // Detached cmd that: waits 2s, stops the service, starts it again,
        // and falls back to LocalSystem if the new account fails to start.
        // `start "" /B cmd /c ...` would be one alternative, but a plain
        // `cmd /c` with UseShellExecute=true produces the same detachment.
        const string script =
            "/c timeout /t 2 /nobreak > nul & " +
            "net stop HirschNotify & " +
            "net start HirschNotify & " +
            "if errorlevel 1 ( " +
                "sc config HirschNotify obj= LocalSystem password= \"\" & " +
                "net start HirschNotify " +
            ")";

        var psi = new ProcessStartInfo("cmd.exe", script)
        {
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        Process.Start(psi);

        WriteEventLog("Detached restart helper spawned.", EventLogEntryType.Information);
    }

    private static void WriteEventLog(string message, EventLogEntryType type)
    {
        try
        {
            if (!EventLog.SourceExists(EventLogSource))
                EventLog.CreateEventSource(EventLogSource, "Application");
            EventLog.WriteEntry(EventLogSource, message, type);
        }
        catch
        {
            // EventLog writes require admin on some flavours; never fail
            // the account change just because we couldn't log it.
        }
    }
}
