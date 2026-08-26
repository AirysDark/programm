# Programm Scanner

A Windows desktop application for Visual Studio 2022 that scans installed programs and displays them in a searchable list.

## Features

- Scans 64-bit installed-program registry entries
- Scans 32-bit installed-program registry entries
- Scans current-user installed applications
- Shows program name, version, publisher, install location and source
- Search/filter results instantly
- Double-click a program to open its installation folder when available
- Export results to CSV

## Build

1. Open `ProgrammScanner/ProgrammScanner.csproj` in Visual Studio 2022.
2. Install the .NET 8 SDK if Visual Studio prompts for it.
3. Select `Release` or `Debug`.
4. Build and run.

## Planned improvements

- Program Files folder scanning
- Microsoft Store app detection
- Folder size calculation
- Uninstall button
- JSON export
- Duplicate and orphaned installation detection
