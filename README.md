# Store App Update Blocker

Lightweight utility that cancels Microsoft Store downloads and updates for configured apps.

It supports two runtime modes:

- `--event-hook`: listen for `AppInstallManager.ItemStatusChanged` notifications
- `--queue-scan`: poll `AppInstallItems` at a configurable interval

The executable is a normal console app so you can verify that it really started. If you want it silent at startup, add `--background`.

## Requirements

- Windows 10 build 19041 or later, or Windows 11
- .NET 8 SDK or newer
- Windows 10 or 11 SDK if your machine needs extra WinRT metadata during build

Relevant files:

- Project file: [StoreAppUpdateBlocker.csproj](StoreAppUpdateBlocker.csproj)
- Main source: [Program.cs](Program.cs)

## Build

From the repo root:

```cmd
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

Published output:

```text
bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\
```

## Configure blocked apps

Edit the `BlockedApps` array in [Program.cs](Program.cs), then rebuild:

```csharp
private static readonly string[] BlockedApps = new[]
{
    "microsoft.windowscommunicationsapps",
    "Microsoft.Office.OneNote",
    "GroupMe.GroupMe"
};
```

## Run

Event hook mode is the default:

```cmd
StoreAppUpdateBlocker.exe
```

Queue scan fallback:

```cmd
StoreAppUpdateBlocker.exe --queue-scan
```

Custom scan interval:

```cmd
StoreAppUpdateBlocker.exe --queue-scan --scan-interval 2
```

Hide the console window after startup:

```cmd
StoreAppUpdateBlocker.exe --background
```

Hidden queue scan mode:

```cmd
StoreAppUpdateBlocker.exe --queue-scan --background
```

Replace an already-running blocker instance, then continue with this launch:

```cmd
StoreAppUpdateBlocker.exe --background
```

Exit immediately instead of replacing a running blocker instance:

```cmd
StoreAppUpdateBlocker.exe --background --exit-if-running
```

## Logs

The app does not write a log file by default. Pass `--log` if you want it to create:

```text
%LOCALAPPDATA%\StoreAppUpdateBlocker\StoreAppUpdateBlocker.log
```

That can be useful if you want a persistent startup record while running hidden with `--background`.

## Startup options

### Startup folder

Run `install.bat` from the published folder to create the Startup shortcut and immediately launch the blocker. A new launch replaces any already-running blocker instance by default, so a newer copy takes over cleanly during updates.

Create a shortcut manually and set arguments if you want hidden mode:

```powershell
$exe = "C:\full\path\to\StoreAppUpdateBlocker.exe"
$args = "--background"
$startup = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Startup\StoreAppUpdateBlocker.lnk"
$ws = New-Object -ComObject WScript.Shell
$sc = $ws.CreateShortcut($startup)
$sc.TargetPath = $exe
$sc.Arguments = $args
$sc.WorkingDirectory = Split-Path $exe
$sc.Save()
```

### Task Scheduler

```cmd
schtasks /Create /SC ONLOGON /TN "StoreAppUpdateBlocker" /TR "\"C:\full\path\StoreAppUpdateBlocker.exe\" --background" /RL HIGHEST /F
```

### Registry Run key

```cmd
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v StoreAppUpdateBlocker /t REG_SZ /d "\"C:\full\path\StoreAppUpdateBlocker.exe\" --background" /f
```

## Notes

- `--event-hook` is the lowest-idle option.
- `--queue-scan` is available if the event hook proves unreliable on your machine.
- The app keeps a single active instance.
- A new launch replaces the currently-running blocker instance by default, which is useful during updates.
- `--exit-if-running` restores the older behavior and exits when another instance is already active.
- The app only writes a log file when `--log` is supplied.
