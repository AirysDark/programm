using Microsoft.Win32;
using ProgrammScanner.Models;

namespace ProgrammScanner.Services;

public static class ProgramScannerService
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public static List<InstalledProgram> Scan()
    {
        var results = new Dictionary<string, InstalledProgram>(StringComparer.OrdinalIgnoreCase);

        ScanRegistry(RegistryHive.LocalMachine, RegistryView.Registry64, results, "Registry 64-bit");
        ScanRegistry(RegistryHive.LocalMachine, RegistryView.Registry32, results, "Registry 32-bit");
        ScanRegistry(RegistryHive.CurrentUser, RegistryView.Default, results, "Current User");

        return results.Values
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ScanRegistry(
        RegistryHive hive,
        RegistryView view,
        Dictionary<string, InstalledProgram> results,
        string source)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstallKey = baseKey.OpenSubKey(UninstallPath);
            if (uninstallKey is null) return;

            foreach (var subKeyName in uninstallKey.GetSubKeyNames())
            {
                try
                {
                    using var appKey = uninstallKey.OpenSubKey(subKeyName);
                    if (appKey is null) continue;

                    var name = appKey.GetValue("DisplayName")?.ToString();
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var program = new InstalledProgram
                    {
                        Name = name.Trim(),
                        Version = appKey.GetValue("DisplayVersion")?.ToString() ?? "",
                        Publisher = appKey.GetValue("Publisher")?.ToString() ?? "",
                        InstallLocation = appKey.GetValue("InstallLocation")?.ToString() ?? "",
                        UninstallCommand = appKey.GetValue("UninstallString")?.ToString() ?? "",
                        Source = source
                    };

                    var key = $"{program.Name}|{program.Version}|{program.InstallLocation}";
                    results.TryAdd(key, program);
                }
                catch
                {
                    // Ignore individual broken registry entries.
                }
            }
        }
        catch
        {
            // Access can be denied for some registry areas.
        }
    }
}
