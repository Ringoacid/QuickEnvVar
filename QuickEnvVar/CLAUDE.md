# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

QuickEnvVar is a Windows WPF desktop utility for managing User and System PATH environment variables. It can be launched from a folder context menu (via Windows Explorer) with a folder path as a command-line argument.

## Build and Run

```bash
dotnet build
dotnet run -- "C:\some\path"   # argument is the initial folder path to add
```

**Installer:** Built with Inno Setup using `QuickEnvVar.iss` in the repo root. Requires a Release build output in `bin/Release/net10.0-windows/` before compiling the installer.

No test project exists.

## Architecture

The app is a single-window WPF application targeting .NET 10.0-windows.

**`PathEntry.cs`** — Model. Implements `INotifyPropertyChanged`. Holds a `Path` string plus two computed flags: `Exists` (via `Directory.Exists`) and `IsDuplicate`.

**`MainWindow.xaml` / `MainWindow.xaml.cs`** — Everything else. Dual-pane layout (User PATH left, System PATH right). Key behaviors:
- Reads/writes User PATH from `HKCU\Environment` directly via `Microsoft.Win32.Registry`.
- Reads System PATH from `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment`.
- Writes System PATH via an elevated PowerShell script (UAC prompt), because the process runs unprivileged.
- Maintains two `ObservableCollection<PathEntry>` instances and a parallel filtered collection for search.
- Duplicate detection is cross-list (case-insensitive): a path present in both User and System is flagged orange.
- Non-existent paths are flagged red via a `DataTrigger` in the `ItemTemplate`.
- Edit mode for System PATH uses a snapshot/restore pattern: a copy is taken on entry, and discarded or committed on exit.
- Export writes a timestamped `.txt` backup of the current PATH to a user-chosen location.

**`App.xaml.cs`** — Reads `args[0]` (if present) and passes it to `MainWindow` as the initial folder path to pre-populate.
