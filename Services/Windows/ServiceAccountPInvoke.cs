using System.Runtime.InteropServices;

namespace HirschNotify.Services.Windows;

/// <summary>
/// Raw Win32 interop surface used by <see cref="ServiceAccountManager"/>.
/// Grouped here so the manager's C# stays readable.
/// </summary>
internal static partial class ServiceAccountPInvoke
{
    // ── advapi32: service control manager ──────────────────────────────

    public const uint SC_MANAGER_CONNECT = 0x0001;
    public const uint SERVICE_CHANGE_CONFIG = 0x0002;
    public const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial IntPtr OpenSCManager(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial IntPtr OpenService(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ChangeServiceConfig(
        IntPtr hService,
        uint dwServiceType,
        uint dwStartType,
        uint dwErrorControl,
        string? lpBinaryPathName,
        string? lpLoadOrderGroup,
        IntPtr lpdwTagId,
        string? lpDependencies,
        string? lpServiceStartName,
        string? lpPassword,
        string? lpDisplayName);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseServiceHandle(IntPtr hSCObject);

    // ── advapi32: LogonUser validation ─────────────────────────────────

    public const int LOGON32_LOGON_SERVICE = 5;
    public const int LOGON32_PROVIDER_DEFAULT = 0;

    [LibraryImport("advapi32.dll", EntryPoint = "LogonUserW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool LogonUser(
        string lpszUsername,
        string? lpszDomain,
        string lpszPassword,
        int dwLogonType,
        int dwLogonProvider,
        out IntPtr phToken);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(IntPtr hObject);

    // ── advapi32: LSA policy / account rights ──────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    public struct LSA_OBJECT_ATTRIBUTES
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LSA_UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    public const uint POLICY_CREATE_ACCOUNT = 0x00000010;
    public const uint POLICY_LOOKUP_NAMES   = 0x00000800;

    [LibraryImport("advapi32.dll", SetLastError = true)]
    public static partial uint LsaOpenPolicy(
        IntPtr SystemName,
        ref LSA_OBJECT_ATTRIBUTES ObjectAttributes,
        uint DesiredAccess,
        out IntPtr PolicyHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    public static partial uint LsaAddAccountRights(
        IntPtr PolicyHandle,
        byte[] AccountSid,
        LSA_UNICODE_STRING[] UserRights,
        uint CountOfRights);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    public static partial uint LsaClose(IntPtr ObjectHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    public static partial int LsaNtStatusToWinError(uint Status);

    // ── advapi32: SID lookup ───────────────────────────────────────────
    // DllImport (not LibraryImport) because the source-generated variant
    // doesn't support StringBuilder parameters.

    [DllImport("advapi32.dll", EntryPoint = "LookupAccountNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool LookupAccountName(
        string? lpSystemName,
        string lpAccountName,
        byte[]? Sid,
        ref uint cbSid,
        System.Text.StringBuilder? ReferencedDomainName,
        ref uint cchReferencedDomainName,
        out int peUse);
}
