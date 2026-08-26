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

    private async void FindOnlineButton_Click(object sender, RoutedEventArgs e)
    {
        var program = ProgramsGrid.SelectedItem as InstalledProgram;
        if (program is null)
        {
            MessageBox.Show("Select a program first.", "Online Lookup", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await LookupProgramsAsync([program]);
    }

    private async void FindOnlineCheckedButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = _programs.Where(p => p.IsOnlineSelected).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("Tick one or more programs in the left-hand checkbox column first.",
                "Online Lookup", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await LookupProgramsAsync(selected);
    }

    private async Task LookupProgramsAsync(List<InstalledProgram> programs)
    {
        SetLookupControlsEnabled(false);
        var completed = 0;
        var failures = 0;

        try
        {
            foreach (var program in programs)
            {
                completed++;
                StatusText.Text = $"Searching {completed}/{programs.Count}: {program.Name}";

                try
                {
                    await OnlineProgramLookupService.LookupAsync(program);
                }
                catch
                {
                    failures++;
                    program.OnlineStatus = "Lookup failed";
                }

                ProgramsGrid.Items.Refresh();
            }

            var success = programs.Count - failures;
            StatusText.Text = $"Online lookup complete: {success} processed, {failures} failed.";
        }
        finally
        {
            SetLookupControlsEnabled(true);
            ProgramsGrid.Items.Refresh();
        }
    }

    private void SetLookupControlsEnabled(bool enabled)
    {
        FindOnlineButton.IsEnabled = enabled;
        FindOnlineCheckedButton.IsEnabled = enabled;
        SelectAllCheckBox.IsEnabled = enabled;
    }

    private void SelectAllCheckBox_Click(object sender, RoutedEventArgs e)
    {
        var isChecked = SelectAllCheckBox.IsChecked == true;
        foreach (var program in _programs)
            program.IsOnlineSelected = isChecked;

        ProgramsGrid.Items.Refresh();
    }

    private void OpenWebsiteButton_Click(object sender, RoutedEventArgs e)
    {
        OpenSelectedUrl(p => p.OfficialWebsite, "No official website has been found for this program yet.");
    }

    private void OpenDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        OpenSelectedUrl(p => p.DownloadUrl, "No download link has been found for this program yet.");
    }

    private void OpenInBrowserMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem || menuItem.Parent is not ContextMenu contextMenu) return;

        string? url = contextMenu.PlacementTarget switch
        {
            Button button when button.Tag?.ToString() == "OfficialWebsite" =>
                (ProgramsGrid.SelectedItem as InstalledProgram)?.OfficialWebsite,
            Button button when button.Tag?.ToString() == "DownloadUrl" =>
                (ProgramsGrid.SelectedItem as InstalledProgram)?.DownloadUrl,
            DataGridCell cell when cell.DataContext is InstalledProgram program && cell.Tag?.ToString() == "OfficialWebsite" => program.OfficialWebsite,
            DataGridCell cell when cell.DataContext is InstalledProgram program && cell.Tag?.ToString() == "DownloadUrl" => program.DownloadUrl,
            _ => null
        };

        OpenUrlInBrowser(url);
    }

    private void OpenSelectedUrl(Func<InstalledProgram, string> selector, string emptyMessage)
    {
        var program = ProgramsGrid.SelectedItem as InstalledProgram;
        if (program is null) return;

        var url = selector(program);
        if (string.IsNullOrWhiteSpace(url))
        {
            MessageBox.Show(emptyMessage, "Online Lookup", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        OpenUrlInBrowser(url);
    }

    private void OpenUrlInBrowser(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            MessageBox.Show("No link is available for this item.", "Open in Browser", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            MessageBox.Show("The selected link is not a valid HTTP or HTTPS URL.", "Open in Browser", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
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
                p.ParentProgram.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                p.OfficialWebsite.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                p.DownloadUrl.Contains(search, StringComparison.OrdinalIgnoreCase))
              .ToList();

        ProgramsGrid.ItemsSource = filtered;
    }

    private void ProgramsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var program = ProgramsGrid.SelectedItem as InstalledProgram;
        if (program == null || string.IsNullOrWhiteSpace(program.ActualInstallPath) || !Directory.Exists(program.ActualInstallPath)) return;

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{program.ActualInstallPath}\"",
            UseShellExecute = true
        });
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "CSV files (*.csv)|*.csv", FileName = "installed-programs.csv" };
        if (dialog.ShowDialog() != true) return;

        var rows = new List<string>
        {
            "Program,Version,Publisher,Install Location,Actual Path,Location Source,Parent Program,Official Website,Download URL,Online Source,Online Status,Source"
        };

        rows.AddRange(_programs.Select(p => string.Join(",",
            Csv(p.Name), Csv(p.Version), Csv(p.Publisher), Csv(p.InstallLocation), Csv(p.ActualInstallPath),
            Csv(p.LocationSource), Csv(p.ParentProgram), Csv(p.OfficialWebsite), Csv(p.DownloadUrl),
            Csv(p.OnlineSource), Csv(p.OnlineStatus), Csv(p.Source))));

        File.WriteAllText(dialog.FileName, string.Join(Environment.NewLine, rows), new UTF8Encoding(true));
        StatusText.Text = $"Exported {_programs.Count} programs.";
    }

    private static string Csv(string value) => $"\"{(value ?? "").Replace("\"", "\"\"")}\"";
}
