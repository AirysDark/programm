using Microsoft.Win32;
using ProgrammScanner.Models;
using System.IO;
using System.Text.RegularExpressions;

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

    private static void ScanRegistry(RegistryHive hive, RegistryView view,
        Dictionary<string, InstalledProgram> results, string source)
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

                    var rawLocation = appKey.GetValue("InstallLocation")?.ToString() ?? "";
                    var uninstallCommand = appKey.GetValue("UninstallString")?.ToString() ?? "";
                    var resolvedPath = ResolveActualPath(rawLocation, uninstallCommand);
                    var locationInfo = GetFriendlyLocation(resolvedPath, name);

                    var program = new InstalledProgram
                    {
                        Name = name.Trim(),
                        Version = appKey.GetValue("DisplayVersion")?.ToString() ?? "",
                        Publisher = appKey.GetValue("Publisher")?.ToString() ?? "",
                        InstallLocation = locationInfo.FriendlyLocation,
                        ActualInstallPath = resolvedPath,
                        ParentProgram = locationInfo.ParentProgram,
                        UninstallCommand = uninstallCommand,
                        Source = source
                    };

                    var key = $"{program.Name}|{program.Version}|{program.ActualInstallPath}";
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

    private static string ResolveActualPath(string installLocation, string uninstallCommand)
    {
        installLocation = Environment.ExpandEnvironmentVariables(installLocation?.Trim() ?? "");
        if (!string.IsNullOrWhiteSpace(installLocation))
        {
            return installLocation.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        var executablePath = ExtractExecutablePath(uninstallCommand);
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            return Path.GetDirectoryName(executablePath) ?? "";
        }

        return "";
    }

    private static string ExtractExecutablePath(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return "";
        command = Environment.ExpandEnvironmentVariables(command.Trim());

        var quoted = Regex.Match(command, "^\\s*\\\"([^\\\"]+?\\.(?:exe|msi|cmd|bat))\\\"", RegexOptions.IgnoreCase);
        if (quoted.Success) return quoted.Groups[1].Value;

        var unquoted = Regex.Match(command, "^\\s*([^\\s]+?\\.(?:exe|msi|cmd|bat))(?=\\s|$)", RegexOptions.IgnoreCase);
        return unquoted.Success ? unquoted.Groups[1].Value : "";
    }

    private static (string FriendlyLocation, string ParentProgram) GetFriendlyLocation(string path, string programName)
    {
        if (string.IsNullOrWhiteSpace(path))
            return (programName, "");

        var normalized = path.Replace('/', '\\').TrimEnd('\\');
        var lower = normalized.ToLowerInvariant();

        // Visual Studio 2022 installations and components.
        const string vsMarker = @"\microsoft visual studio\2022\";
        var vsIndex = lower.IndexOf(vsMarker, StringComparison.Ordinal);
        if (vsIndex >= 0)
        {
            var afterYear = normalized[(vsIndex + vsMarker.Length)..];
            var edition = afterYear.Split('\\', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            var parent = string.IsNullOrWhiteSpace(edition)
                ? "Visual Studio 2022"
                : $"Visual Studio 2022 {edition}";

            return (parent, parent);
        }

        // VS Installer packages/cache should still be clearly identified as Visual Studio.
        if (lower.Contains(@"\microsoft\visualstudio\packages\") ||
            lower.Contains(@"\microsoft visual studio\installer\") ||
            programName.Contains("visual studio", StringComparison.OrdinalIgnoreCase))
        {
            return ("Visual Studio 2022 / Visual Studio component", "Visual Studio 2022");
        }

        // Windows SDKs are commonly shared installations and registry entries often point deep inside them.
        var sdk10 = lower.IndexOf(@"\windows kits\10\", StringComparison.Ordinal);
        if (sdk10 >= 0)
        {
            var root = normalized[..(sdk10 + @"\Windows Kits\10".Length)];
            return ("Windows 10 SDK", "Windows 10 SDK");
        }

        var sdk11 = lower.IndexOf(@"\windows kits\11\", StringComparison.Ordinal);
        if (sdk11 >= 0)
        {
            var root = normalized[..(sdk11 + @"\Windows Kits\11".Length)];
            return ("Windows 11 SDK", "Windows 11 SDK");
        }

        // If the path is inside Program Files, keep only the product root when possible.
        foreach (var root in GetProgramFilesRoots())
        {
            if (!normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;

            var relative = normalized[root.Length..].TrimStart('\\');
            var parts = relative.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return (root, "");

            var productRoot = parts.Length >= 2
                ? Path.Combine(root, parts[0], parts[1])
                : Path.Combine(root, parts[0]);

            return (productRoot, "");
        }

        return (normalized, "");
    }

    private static IEnumerable<string> GetProgramFilesRoots()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"\Programs"
        };

        return roots
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(x => x.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }
}
