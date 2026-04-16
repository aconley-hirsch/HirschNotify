namespace HirschNotify.Services;

// Runtime data locations. On Windows the data root is %ProgramData%
// \HirschNotify per the conventional Windows service split (code under
// Program Files, mutable state under ProgramData). On other platforms
// the data root is AppContext.BaseDirectory so dev runs on macOS keep
// their DB and logs next to the bin/Debug output.
public static class AppPaths
{
    public static string DataRoot { get; } = OperatingSystem.IsWindows()
        ? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "HirschNotify")
        : AppContext.BaseDirectory;

    public static string LogsDir => Path.Combine(DataRoot, "Logs");

    public static string KeysDir => Path.Combine(DataRoot, "Keys");
}
