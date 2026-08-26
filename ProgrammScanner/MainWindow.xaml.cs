using Microsoft.Win32;
using ProgrammScanner.Models;
using ProgrammScanner.Services;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace ProgrammScanner;

public partial class MainWindow : Window
{
    private List<InstalledProgram> _programs = [];

    public MainWindow()
    {
        InitializeComponent();
        StatusText.Text = "Ready to scan.";
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        StatusText.Text = "Scanning installed programs and resolving locations...";

        try
        {
            _programs = await Task.Run(ProgramScannerService.Scan);
            ApplyFilter();
            var foundLocations = _programs.Count(p => !string.IsNullOrWhiteSpace(p.ActualInstallPath));
            StatusText.Text = $"Found {_programs.Count} programs. Resolved {foundLocations} install locations.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Scan Error", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Scan failed.";
        }
        finally
        {
            ScanButton.IsEnabled = true;
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var search = SearchBox.Text.Trim();
        var filtered = string.IsNullOrWhiteSpace(search)
            ? _programs
            : _programs.Where(p =>
                p.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                p.Version.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                p.Publisher.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                p.InstallLocation.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                p.ActualInstallPath.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                p.ParentProgram.Contains(search, StringComparison.OrdinalIgnoreCase))
              .ToList();

        ProgramsGrid.ItemsSource = filtered;
    }

    private void ProgramsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var program = ProgramsGrid.SelectedItem as InstalledProgram;
        if (program == null ||
            string.IsNullOrWhiteSpace(program.ActualInstallPath) ||
            !Directory.Exists(program.ActualInstallPath))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{program.ActualInstallPath}\"",
            UseShellExecute = true
        });
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv",
            FileName = "installed-programs.csv"
        };

        if (dialog.ShowDialog() != true) return;

        var rows = new List<string>
        {
            "Program,Version,Publisher,Install Location,Actual Path,Location Source,Parent Program,Source"
        };

        rows.AddRange(_programs.Select(p => string.Join(",",
            Csv(p.Name),
            Csv(p.Version),
            Csv(p.Publisher),
            Csv(p.InstallLocation),
            Csv(p.ActualInstallPath),
            Csv(p.LocationSource),
            Csv(p.ParentProgram),
            Csv(p.Source))));

        File.WriteAllText(dialog.FileName, string.Join(Environment.NewLine, rows), new UTF8Encoding(true));
        StatusText.Text = $"Exported {_programs.Count} programs.";
    }

    private static string Csv(string value) => $"\"{(value ?? "").Replace("\"", "\"\"")}\"";
}
