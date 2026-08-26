namespace ProgrammScanner.Models;

public class InstalledProgram
{
    public string Name { get; set; } = "Unknown";
    public string Version { get; set; } = "";
    public string Publisher { get; set; } = "";

    // Friendly location shown in the main grid.
    public string InstallLocation { get; set; } = "";

    // Exact path reported by Windows or resolved from the uninstall command.
    public string ActualInstallPath { get; set; } = "";

    // Parent product detected from the path, for example Visual Studio 2022 Community.
    public string ParentProgram { get; set; } = "";

    public string UninstallCommand { get; set; } = "";
    public string Source { get; set; } = "";
}
