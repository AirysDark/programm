namespace ProgrammScanner.Models;

public class InstalledProgram
{
    public string Name { get; set; } = "Unknown";
    public string Version { get; set; } = "";
    public string Publisher { get; set; } = "";

    // Friendly location shown in the main grid.
    public string InstallLocation { get; set; } = "";

    // Exact path resolved from Windows registry or filesystem discovery.
    public string ActualInstallPath { get; set; } = "";

    // Explains how ActualInstallPath was found.
    public string LocationSource { get; set; } = "Not Found";

    // Parent product detected from the path, for example Visual Studio 2022 Community.
    public string ParentProgram { get; set; } = "";

    public string UninstallCommand { get; set; } = "";
    public string Source { get; set; } = "";
}
