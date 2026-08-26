using Microsoft.Win32;
using ProgrammScanner.Models;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace ProgrammScanner.Services;

public static class ProgramScannerService
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string AppPathsPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";

    private sealed record LocationResult(string Path, string Source);

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

                    var publisher = appKey.GetValue("Publisher")?.ToString() ?? "";
                    var rawLocation = appKey.GetValue("InstallLocation")?.ToString() ?? "";
                    var uninstallCommand = appKey.GetValue("UninstallString")?.ToString() ?? "";
                    var displayIcon = appKey.GetValue("DisplayIcon")?.ToString() ?? "";

                    var location = ResolveLocation(
                        rawLocation,
                        uninstallCommand,
                        displayIcon,
                        name.Trim(),
                        publisher,
                        hive,
                        view);

                    var locationInfo = GetFriendlyLocation(location.Path, name.Trim());

                    var program = new InstalledProgram
                    {
                        Name = name.Trim(),
                        Version = appKey.GetValue("DisplayVersion")?.ToString() ?? "",
                        Publisher = publisher,
                        InstallLocation = locationInfo.FriendlyLocation,
                        ActualInstallPath = location.Path,
                        LocationSource = location.Source,
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

    private static LocationResult ResolveLocation(
        string installLocation,
        string uninstallCommand,
        string displayIcon,
        string programName,
        string publisher,
        RegistryHive hive,
        RegistryView view)
    {
        // 1. Installer-provided location.
        var path = NormalizeExistingPath(installLocation, false);
        if (!string.IsNullOrWhiteSpace(path))
            return new LocationResult(path, "Registry InstallLocation");

        // 2. Uninstaller executable location.
        path = NormalizeExistingPath(ExtractExecutablePath(uninstallCommand), true);
        if (!string.IsNullOrWhiteSpace(path))
            return new LocationResult(path, "Uninstall Command");

        // 3. DisplayIcon often points directly to the main application executable.
        path = NormalizeExistingPath(ExtractExecutablePath(displayIcon), true);
        if (!string.IsNullOrWhiteSpace(path))
            return new LocationResult(path, "DisplayIcon");

        // 4. Windows App Paths registry lookup.
        path = FindInAppPaths(programName, hive, view);
        if (!string.IsNullOrWhiteSpace(path))
            return new LocationResult(path, "Windows App Paths");

        // 5. Known Visual Studio, SDK and other structured installation roots.
        path = FindKnownLocation(programName, publisher);
        if (!string.IsNullOrWhiteSpace(path))
            return new LocationResult(path, "Known Default Location");

        // 6. Search only likely top-level application folders and validate candidates.
        path = FindByFolderAndExecutableMatch(programName, publisher);
        if (!string.IsNullOrWhiteSpace(path))
            return new LocationResult(path, "Folder / Executable Match");

        return new LocationResult("", "Not Found");
    }

    private static string NormalizeExistingPath(string value, bool valueIsFile)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        value = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
        if (valueIsFile)
        {
            if (File.Exists(value)) return Path.GetDirectoryName(value) ?? "";
            return "";
        }

        if (Directory.Exists(value))
            return value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (File.Exists(value))
            return Path.GetDirectoryName(value) ?? "";

        return "";
    }

    private static string FindInAppPaths(string programName, RegistryHive hive, RegistryView view)
    {
        var names = BuildSearchTokens(programName)
            .Select(x => x + ".exe")
            .Append(programName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? programName : "")
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var appPaths = baseKey.OpenSubKey(AppPathsPath);
            if (appPaths is null) return "";

            foreach (var subKeyName in appPaths.GetSubKeyNames())
            {
                if (!names.Any(n => string.Equals(n, subKeyName, StringComparison.OrdinalIgnoreCase)))
                    continue;

                using var appKey = appPaths.OpenSubKey(subKeyName);
                var executable = appKey?.GetValue("")?.ToString() ?? "";
                var location = NormalizeExistingPath(executable, true);
                if (!string.IsNullOrWhiteSpace(location)) return location;
            }
        }
        catch { }

        return "";
    }

    private static string FindKnownLocation(string programName, string publisher)
    {
        var lowerName = programName.ToLowerInvariant();

        if (lowerName.Contains("visual studio") ||
            lowerName.Contains("vs setup") ||
            publisher.Contains("visual studio", StringComparison.OrdinalIgnoreCase))
        {
            var vsRoots = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft Visual Studio", "2022"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "2022"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer")
            };

            foreach (var root in vsRoots)
            {
                if (!Directory.Exists(root)) continue;
                if (Path.GetFileName(root).Equals("2022", StringComparison.OrdinalIgnoreCase))
                {
                    var editions = Directory.GetDirectories(root);
                    if (editions.Length == 1) return editions[0];
                    if (editions.Length > 0) return root;
                }
                return root;
            }
        }

        if (lowerName.Contains("windows sdk") || lowerName.Contains("windows kits"))
        {
            foreach (var root in new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "11")
            })
            {
                if (Directory.Exists(root)) return root;
            }
        }

        return "";
    }

    private static string FindByFolderAndExecutableMatch(string programName, string publisher)
    {
        var tokens = BuildSearchTokens(programName).ToList();
        if (tokens.Count == 0) return "";

        var candidates = new List<string>();
        foreach (var root in GetSearchRoots())
        {
            try
            {
                if (!Directory.Exists(root)) continue;

                // Only inspect top-level vendor/product folders to keep scans fast.
                foreach (var directory in Directory.EnumerateDirectories(root))
                {
                    var folderName = Path.GetFileName(directory);
                    if (MatchesText(folderName, tokens) ||
                        (!string.IsNullOrWhiteSpace(publisher) && folderName.Contains(publisher, StringComparison.OrdinalIgnoreCase)))
                    {
                        candidates.Add(directory);
                    }

                    foreach (var child in SafeEnumerateDirectories(directory))
                    {
                        if (MatchesText(Path.GetFileName(child), tokens)) candidates.Add(child);
                    }
                }
            }
            catch { }
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (ContainsMatchingExecutable(candidate, tokens)) return candidate;
        }

        return "";
    }

    private static bool ContainsMatchingExecutable(string directory, List<string> tokens)
    {
        try
        {
            foreach (var exe in Directory.EnumerateFiles(directory, "*.exe", SearchOption.TopDirectoryOnly))
            {
                var fileName = Path.GetFileNameWithoutExtension(exe);
                if (MatchesText(fileName, tokens)) return true;

                try
                {
                    var info = FileVersionInfo.GetVersionInfo(exe);
                    if (MatchesText(info.ProductName, tokens) ||
                        MatchesText(info.FileDescription, tokens))
                        return true;
                }
                catch { }
            }
        }
        catch { }

        return false;
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try { return Directory.EnumerateDirectories(path); }
        catch { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> GetSearchRoots()
    {
        return new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")
        }
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> BuildSearchTokens(string name)
    {
        var cleaned = Regex.Replace(name ?? "", @"\([^)]*\)|\[[^]]*\]|\b(version|x64|x86|64-bit|32-bit)\b", " ", RegexOptions.IgnoreCase);
        var words = Regex.Matches(cleaned, @"[A-Za-z0-9][A-Za-z0-9._+-]{2,}")
            .Select(m => m.Value)
            .Where(x => !IsNoiseWord(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (words.Count > 0) yield return string.Join(" ", words);
        foreach (var word in words.OrderByDescending(x => x.Length).Take(3)) yield return word;
    }

    private static bool IsNoiseWord(string value) => value.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("Windows", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("Update", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("Runtime", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("Redistributable", StringComparison.OrdinalIgnoreCase);

    private static bool MatchesText(string? value, IEnumerable<string> tokens)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return tokens.Any(token => token.Length >= 3 && value.Contains(token, StringComparison.OrdinalIgnoreCase));
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
        if (string.IsNullOrWhiteSpace(path)) return ("", "");

        var normalized = path.Replace('/', '\\').TrimEnd('\\');
        var lower = normalized.ToLowerInvariant();

        const string vsMarker = @"\microsoft visual studio\2022\";
        var vsIndex = lower.IndexOf(vsMarker, StringComparison.Ordinal);
        if (vsIndex >= 0)
        {
            var afterYear = normalized[(vsIndex + vsMarker.Length)..];
            var edition = afterYear.Split('\\', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            var parent = string.IsNullOrWhiteSpace(edition) ? "Visual Studio 2022" : $"Visual Studio 2022 {edition}";
            return (parent, parent);
        }

        if (lower.Contains(@"\microsoft\visualstudio\packages\") ||
            lower.Contains(@"\microsoft visual studio\installer\"))
            return ("Visual Studio 2022 / Visual Studio component", "Visual Studio 2022");

        if (lower.Contains(@"\windows kits\10\")) return ("Windows 10 SDK", "Windows 10 SDK");
        if (lower.Contains(@"\windows kits\11\")) return ("Windows 11 SDK", "Windows 11 SDK");

        return (normalized, "");
    }
}
