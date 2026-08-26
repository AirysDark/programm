using Microsoft.Win32;
using ProgrammScanner.Models;
using ProgrammScanner.Services;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ProgrammScanner;

public partial class MainWindow : Window
{
    private List<InstalledProgram> _programs = [];

    public MainWindow()
    {
        InitializeComponent();
        StatusText.Text = "Ready to scan or import a CSV file.";
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (e.ClickCount == 2) ToggleMaximizeRestore(); else DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeRestoreButton_Click(object sender, RoutedEventArgs e) => ToggleMaximizeRestore();
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    private void ToggleMaximizeRestore() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Window_StateChanged(object sender, EventArgs e) => MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "▢";

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        StatusText.Text = "Scanning installed programs and resolving locations...";
        try
        {
            _programs = await Task.Run(ProgramScannerService.Scan);
            SelectAllCheckBox.IsChecked = false;
            ApplyFilter();
            StatusText.Text = $"Found {_programs.Count} programs. Resolved {_programs.Count(p => !string.IsNullOrWhiteSpace(p.ActualInstallPath))} install locations.";
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Scan Error", MessageBoxButton.OK, MessageBoxImage.Error); StatusText.Text = "Scan failed."; }
        finally { ScanButton.IsEnabled = true; }
    }

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*", Multiselect = false, Title = "Import Program CSV" };
        if (dialog.ShowDialog() != true) return;

        try
        {
            List<List<string>> rows = ReadCsvFile(dialog.FileName);
            if (rows.Count < 2) throw new InvalidDataException("The CSV file does not contain a header and at least one data row.");

            List<string> headers = rows[0].Select(NormalizeHeader).ToList();
            int nameColumn = FindColumn(headers, "program", "name", "displayname", "application", "app");
            if (nameColumn < 0) throw new InvalidDataException("No Program or Name column was found in the CSV file.");

            var imported = new List<InstalledProgram>();
            foreach (List<string> row in rows.Skip(1))
            {
                if (row.All(string.IsNullOrWhiteSpace)) continue;
                string name = GetColumn(row, nameColumn);
                if (string.IsNullOrWhiteSpace(name)) continue;

                imported.Add(new InstalledProgram
                {
                    Name = name,
                    Version = GetColumn(row, FindColumn(headers, "version")),
                    Publisher = GetColumn(row, FindColumn(headers, "publisher")),
                    InstallLocation = GetColumn(row, FindColumn(headers, "installlocation", "installationlocation", "location")),
                    ActualInstallPath = GetColumn(row, FindColumn(headers, "actualpath", "actualinstallpath", "installpath", "path")),
                    LocationSource = GetColumn(row, FindColumn(headers, "locationsource"), "Imported CSV"),
                    ParentProgram = GetColumn(row, FindColumn(headers, "parentprogram", "parent")),
                    UninstallCommand = GetColumn(row, FindColumn(headers, "uninstallcommand", "uninstall")),
                    OfficialWebsite = GetColumn(row, FindColumn(headers, "officialwebsite", "website", "homepage")),
                    DownloadUrl = GetColumn(row, FindColumn(headers, "downloadurl", "downloadlink", "download")),
                    OnlineSource = GetColumn(row, FindColumn(headers, "onlinesource")),
                    OnlineStatus = GetColumn(row, FindColumn(headers, "onlinestatus"), "Imported"),
                    Source = GetColumn(row, FindColumn(headers, "source"), "Imported CSV")
                });
            }

            _programs = imported;
            SelectAllCheckBox.IsChecked = false;
            ApplyFilter();
            StatusText.Text = $"Imported {_programs.Count} programs from {Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not import the CSV file.\n\n{ex.Message}", "Import CSV", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "CSV import failed.";
        }
    }

    private async void FindOnlineButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProgramsGrid.SelectedItem is not InstalledProgram program) { MessageBox.Show("Select a program first.", "Online Lookup", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        await LookupProgramsAsync([program]);
    }

    private async void FindOnlineCheckedButton_Click(object sender, RoutedEventArgs e)
    {
        List<InstalledProgram> selected = _programs.Where(p => p.IsOnlineSelected).ToList();
        if (selected.Count == 0) { MessageBox.Show("Tick one or more programs first.", "Online Lookup", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        await LookupProgramsAsync(selected);
    }

    private async Task LookupProgramsAsync(List<InstalledProgram> programs)
    {
        SetLookupControlsEnabled(false);
        var completed = 0; var failures = 0;
        try
        {
            foreach (InstalledProgram program in programs)
            {
                StatusText.Text = $"Searching {++completed}/{programs.Count}: {program.Name}";
                try { await OnlineProgramLookupService.LookupAsync(program); } catch { failures++; program.OnlineStatus = "Lookup failed"; }
                ProgramsGrid.Items.Refresh();
            }
            StatusText.Text = $"Online lookup complete: {programs.Count - failures} processed, {failures} failed.";
        }
        finally { SetLookupControlsEnabled(true); ProgramsGrid.Items.Refresh(); }
    }

    private void SetLookupControlsEnabled(bool enabled) { FindOnlineButton.IsEnabled = enabled; FindOnlineCheckedButton.IsEnabled = enabled; SelectAllCheckBox.IsEnabled = enabled; }
    private void SelectAllCheckBox_Checked(object sender, RoutedEventArgs e) => SetAllOnlineSelections(true);
    private void SelectAllCheckBox_Unchecked(object sender, RoutedEventArgs e) => SetAllOnlineSelections(false);
    private void SetAllOnlineSelections(bool selected) { foreach (InstalledProgram program in _programs) program.IsOnlineSelected = selected; ProgramsGrid.Items.Refresh(); }
    private void OpenWebsiteButton_Click(object sender, RoutedEventArgs e) => OpenSelectedUrl(p => p.OfficialWebsite, "No official website has been found for this program yet.");
    private void OpenDownloadButton_Click(object sender, RoutedEventArgs e) => OpenSelectedUrl(p => p.DownloadUrl, "No download link has been found for this program yet.");

    private void OpenInBrowserMenuItem_Click(object sender, RoutedEventArgs e)
    {
        string? url = (ProgramsGrid.SelectedItem as InstalledProgram)?.DownloadUrl;
        if (string.IsNullOrWhiteSpace(url)) url = (ProgramsGrid.SelectedItem as InstalledProgram)?.OfficialWebsite;
        OpenUrlInBrowser(url);
    }

    private void OpenSelectedUrl(Func<InstalledProgram, string> selector, string emptyMessage)
    {
        if (ProgramsGrid.SelectedItem is not InstalledProgram program) return;
        string url = selector(program);
        if (string.IsNullOrWhiteSpace(url)) { MessageBox.Show(emptyMessage, "Online Lookup", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        OpenUrlInBrowser(url);
    }

    private static void OpenUrlInBrowser(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) { MessageBox.Show("No valid HTTP or HTTPS link is available.", "Open in Browser", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();
    private void ApplyFilter()
    {
        string search = SearchBox.Text.Trim();
        ProgramsGrid.ItemsSource = string.IsNullOrWhiteSpace(search) ? _programs : _programs.Where(p => p.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || p.Version.Contains(search, StringComparison.OrdinalIgnoreCase) || p.Publisher.Contains(search, StringComparison.OrdinalIgnoreCase) || p.InstallLocation.Contains(search, StringComparison.OrdinalIgnoreCase) || p.ActualInstallPath.Contains(search, StringComparison.OrdinalIgnoreCase) || p.ParentProgram.Contains(search, StringComparison.OrdinalIgnoreCase) || p.OfficialWebsite.Contains(search, StringComparison.OrdinalIgnoreCase) || p.DownloadUrl.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "CSV files (*.csv)|*.csv", FileName = "installed-programs.csv" };
        if (dialog.ShowDialog() != true) return;
        var rows = new List<string> { "Program,Version,Publisher,Install Location,Actual Path,Location Source,Parent Program,Official Website,Download URL,Online Source,Online Status,Source" };
        rows.AddRange(_programs.Select(p => string.Join(",", Csv(p.Name), Csv(p.Version), Csv(p.Publisher), Csv(p.InstallLocation), Csv(p.ActualInstallPath), Csv(p.LocationSource), Csv(p.ParentProgram), Csv(p.OfficialWebsite), Csv(p.DownloadUrl), Csv(p.OnlineSource), Csv(p.OnlineStatus), Csv(p.Source))));
        File.WriteAllText(dialog.FileName, string.Join(Environment.NewLine, rows), new UTF8Encoding(true));
        StatusText.Text = $"Exported {_programs.Count} programs.";
    }

    private static List<List<string>> ReadCsvFile(string fileName)
    {
        using var reader = new StreamReader(fileName, Encoding.UTF8, true);
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        while (reader.Peek() >= 0)
        {
            char c = (char)reader.Read();
            if (c == '"')
            {
                if (inQuotes && reader.Peek() == '"') { reader.Read(); field.Append('"'); }
                else inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes) { row.Add(field.ToString()); field.Clear(); }
            else if ((c == '\r' || c == '\n') && !inQuotes)
            {
                if (c == '\r' && reader.Peek() == '\n') reader.Read();
                row.Add(field.ToString()); field.Clear();
                if (row.Count > 1 || !string.IsNullOrWhiteSpace(row[0])) rows.Add(row);
                row = new List<string>();
            }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows;
    }

    private static string NormalizeHeader(string value) => new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static int FindColumn(List<string> headers, params string[] names)
    {
        foreach (string name in names)
        {
            int index = headers.IndexOf(NormalizeHeader(name));
            if (index >= 0) return index;
        }
        return -1;
    }

    private static string GetColumn(List<string> row, int index, string fallback = "") => index >= 0 && index < row.Count ? row[index].Trim() : fallback;
    private static string Csv(string value) => $"\"{(value ?? "").Replace("\"", "\"\"")}\"";
}
