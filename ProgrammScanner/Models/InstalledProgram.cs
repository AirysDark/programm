namespace ProgrammScanner.Models;

public class InstalledProgram
{
    public string Name { get; set; } = "Unknown";
    public string Version { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string InstallLocation { get; set; } = "";
    public string UninstallCommand { get; set; } = "";
    public string Source { get; set; } = "";
}
