# Clip Dock

Clip Dock is a Windows desktop application for downloading supported YouTube videos with a simple graphical interface. It uses the yt-dlp engine together with FFmpeg to analyze, select, and download video streams while keeping a local queue, history, and browser-extension bridge for faster workflow.

## Features

- Paste a YouTube URL and inspect available formats
- Detect supported resolutions and output containers
- Estimate file size before downloading
- Download videos with yt-dlp and FFmpeg integration
- Manage a download queue with pause/resume/cancel support
- Keep local history of completed downloads
- Pair a local browser extension for one-click downloads from supported YouTube pages
- Persist application settings locally on the user machine
- Use Windows notifications and system theme integration where available

## Supported Windows versions

- Windows 10
- Windows 11

## Technology stack

- .NET 8 WPF desktop app
- C# / XAML
- yt-dlp for metadata and download orchestration
- FFmpeg for media processing
- Inno Setup for Windows installer packaging
- Chromium Manifest V3 browser extension for local browser integration

## Project structure

```text
ClipDock/
├── src/
│   ├── ClipDock.App/
│   ├── ClipDock.Core/
│   └── ClipDock.Infrastructure/
├── tests/
│   └── ClipDock.Tests/
├── Extension/
├── installer/
├── ClipDock.sln
├── README.md
├── .gitignore
├── logo.png
└── LICENSE
```

## Prerequisites

- Windows 10 or Windows 11
- .NET 8 SDK
- Inno Setup 6 (only for building the Windows installer)
- Access to the internet when the app downloads yt-dlp or FFmpeg on first use

## Development setup

1. Clone the repository.
2. Open the solution in Visual Studio 2022 or another .NET 8-compatible development environment.
3. Restore NuGet packages.
4. Build the solution.

```powershell
dotnet restore
 dotnet build "ClipDock.sln" -c Release
```

## Dependency setup

Clip Dock downloads or locates its engine dependencies at runtime:

- yt-dlp is downloaded from the official yt-dlp GitHub release
- FFmpeg is discovered from PATH when available, or downloaded from the official FFmpeg builds repository if needed
- the app stores runtime tools under the user's local app data folder under `AppData\Local\ClipDock\Tools`

The app does not require a bundled yt-dlp or FFmpeg binary to be checked into the repository.

## How to build locally

To build the desktop app only:

```powershell
dotnet publish "src\ClipDock.App\ClipDock.App.csproj" -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -p:DebugType=None -p:DebugSymbols=false -o "artifacts\publish\1.0.0-win-x64-final"
```

To compile the installer with Inno Setup:

```powershell
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" "installer\ClipDock.iss"
```

The installer output is written to `artifacts\installer`.

## How to run locally

Run the WPF app from Visual Studio, or start the published executable:

```powershell
"artifacts\publish\1.0.0-win-x64-final\ClipDock.exe"
```

## How yt-dlp is integrated

The project uses `EngineManager` to ensure the correct yt-dlp binary exists and verifies the downloaded file using the official SHA-256 manifest. `YtDlpVideoAnalyzer` queries yt-dlp for metadata and format information; `YtDlpDownloadEngine` performs the actual download run.

## How FFmpeg is integrated

`EngineManager` checks for a valid FFmpeg installation on the system. If one is not present, it downloads a supported FFmpeg Windows build and stores it under the application data tools folder. The download engine passes the FFmpeg path to yt-dlp so it can merge/convert media formats as needed.

## Browser-extension communication

The desktop app listens on a local loopback bridge on port 38765. The browser extension sends authenticated requests to the local service using a generated pairing token. The extension can request metadata and queue downloads without leaving the browser context.

## Troubleshooting

- If the app cannot find yt-dlp or FFmpeg, restart the app and allow it to install the missing tools.
- If the browser extension cannot connect, verify that the pairing token matches the one shown in Clip Dock settings.
- If the app fails to start, check the logs under the local app data folder `AppData\Local\ClipDock\Logs`.
- If a YouTube URL is rejected, verify that it is a supported public watch page or Shorts URL.

## Contributing

Contributions are welcome. Please keep changes focused, do not remove the existing application behavior, and update documentation when the app’s tool installation or browser flow changes.

## License

This repository currently does not include a selected open-source license file. Before publishing a public release, choose and add the appropriate license for the project.
