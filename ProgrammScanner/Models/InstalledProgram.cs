namespace ProgrammScanner.Models;

public class InstalledProgram
{
    public string Name { get; set; } = "Unknown";
    public string Version { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string InstallLocation { get; set; } = "";
    public string ActualInstallPath { get; set; } = "";
    public string LocationSource { get; set; } = "Not Found";
    public string ParentProgram { get; set; } = "";
    public string UninstallCommand { get; set; } = "";
    public string Source { get; set; } = "";

    // Online lookup results. These are discovered only when the user requests a lookup.
    public string OfficialWebsite { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string OnlineSource { get; set; } = "";
    public string OnlineStatus { get; set; } = "Not searched";
}
