# FtpZilla

FtpZilla is a lightweight Windows file-transfer client for **FTP** and **SFTP**. Connect to a remote server and move files between it and your PC, using native Windows shell icons and a Mica-backed look.

FtpZilla was called **Winzilla** before version 1.1.0. Installing FtpZilla replaces Winzilla, and your saved connections move to the new data folder automatically on first launch.

It comes in three variants:

| Folder | Description |
|---|---|
| `winui3` | Modern WinUI 3 app (self-contained, .NET 10) |
| `winforms-core` | WinForms on modern .NET (self-contained, single file) |
| `winforms-netfx` | WinForms on .NET Framework 4.8 |

## Install (WinUI 3)

Requires Windows 10 1809 or later. Installs per user, no admin rights needed.

### Option 1: PowerShell one-liner

```powershell
& ([scriptblock]::Create((irm "https://raw.githubusercontent.com/judgewest2000/ftpzilla/main/install.ps1")))
```

This downloads the latest release for your architecture (x64, ARM64 or x86) to `%LOCALAPPDATA%\Programs\FtpZilla`, then adds a Start Menu shortcut and an Apps & Features entry.

Options (append after the closing parentheses):

- `-Version v1.0.0` installs a specific release
- `-InstallDir <path>` installs somewhere else
- `-Uninstall` removes FtpZilla

### Option 2: Setup.exe

Download `FtpZilla-Setup-<arch>.exe` from the [latest release](https://github.com/judgewest2000/ftpzilla/releases/latest) and run it.

### Uninstall

Use Windows Settings > Apps, or run the one-liner above with `-Uninstall`.

## About

FtpZilla is open source, released under the [MIT License](LICENSE). Copyright (c) 2026 James West. In the WinUI 3 app, click the **ⓘ** button at the bottom-right of the Connections sidebar to see the version, license and keyboard shortcuts. From there, **Check for updates** looks for a new release on demand, and **Release notes** shows what changed in each version (from [CHANGELOG.md](CHANGELOG.md)).

The Connections sidebar can be resized by dragging its right edge; double-click the edge to reset it.

## Saved connections and groups

The WinUI 3 app lists saved FTP/SFTP connections in a sidebar, organised into coloured groups (Production, Develop and Read-only by default). Double-click a connection to connect. Right-click a connection or group to edit, duplicate, reorder or delete it.

- When you connect to a server, the title bar and remote pane take on its group's colour and show the group name, so you can see where you are.
- Groups marked **Read-only** allow browsing and downloading only. Upload, new folder, rename, delete, cut, paste and drop are all blocked.

Everything is stored under `%LOCALAPPDATA%\FtpZilla`:

| File | Contents |
| --- | --- |
| `connections.json` | Groups and connections. Saved passwords are encrypted with Windows DPAPI (current-user scope) and can't be decrypted by another Windows account or on another PC. |
| `settings.json` | The last quick-connect details (no password) and trusted SFTP host keys. |

Files are written atomically. An unreadable `connections.json` is kept as `connections.json.corrupt-<timestamp>` and replaced with the default groups.

Run the storage tests with `dotnet test tests\FtpZilla.Tests`.

## Updates

On startup, the WinUI 3 app checks GitHub for a newer release and asks whether to update. Updating downloads the matching `FtpZilla-Setup-<arch>.exe`, verifies its SHA-256 checksum, upgrades in place and restarts the app.

## Build from source

Requires the .NET 10 SDK.

```powershell
# Build every variant into .\dist
.\build-all.ps1 -Zip -Clean

# WinUI 3 only
cd winui3
.\publish-release.ps1 -Platform x64 -Zip
```

## Publish a release

Requires [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`) and the GitHub CLI.

```powershell
cd winui3\installer
.\build-installer.ps1 -Platform x64 -Version 1.0.0
gh release create v1.0.0 ..\publish\FtpZilla-win-x64.zip ..\publish\FtpZilla-Setup-x64.exe --title v1.0.0
```

The zip must be named `FtpZilla-win-<arch>.zip` for `install.ps1` to find it. Repeat for `ARM64` and `x86` as needed.
