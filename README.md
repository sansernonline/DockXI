<p align="center">
  <img src="DockXI/assets/icon.svg" alt="DockXI" width="120" height="120"/>
</p>

# DockXI — Floating Dock for Windows

A lightweight, macOS-style floating dock for Windows. Built on **WPF + .NET 8**.

![DockXI](mockups/DockXI%20—%20UI%20Mockups%20v1.html)

> Pin your apps, folders, and files in a floating dock that hovers on any screen edge.
> Drag to reorder, hover to lift, right-click to manage — clean, fast, native.

---

## Features

- **Floating dock** — always on top, position on any screen edge (Top / Bottom / Left / Right)
- **Auto-hide + reveal zone** — dock slides off-screen when idle, peeks back when you brush the edge
- **Hover lift** — icons rise smoothly when the cursor passes over them
- **Drag-to-reorder** — neighbouring icons split aside with orientation-aware push-aside and insert separator
- **Drag-in to pin** — drop a file / folder from Explorer onto the dock to pin it (works as Admin via UAC bypass)
- **Drag-out to unpin** — pull a tile off the dock to remove it; pop-in / shrink-out bounce animations
- **Running indicator** — accent dot under tiles whose app is running
- **Broken target badge** — red **!** when the pinned target file is missing
- **Per-tile context menu** — right-click an icon to delete it
- **Activity log** — every pin / unpin / launch / position change recorded to `logs/activity.log` (rotates at 1 MB)
- **Dark theme** — matches Windows 11 dark style, transparent rounded plate

## Quick Start

Requires **Visual Studio 2022** with the **".NET desktop development"** workload.

```powershell
git clone <repo-url>
cd "DockXI - Floating Dock\DockXI"
dotnet restore
dotnet build DockXI.sln -c Release
dotnet run --project src\DockXI.WpfShell
```

Or open `DockXI/DockXI.sln` in VS and press **F5**.

## Project Layout

```
DockXI/                ← Visual Studio solution
   src/
      DockXI.Core/       ← Domain services + interfaces (Abstractions/)
      DockXI.WpfShell/   ← WPF app (App, MainDockWindow, view-model)
   tests/
      DockXI.Tests/      ← xUnit unit + integration tests
docs/                  ← Requirements spec (.docx)
mockups/               ← UI design mockup (HTML)
```

See **[`DockXI/README.md`](DockXI/README.md)** for architecture, build, and developer guide.

## Tech Stack

- **WPF** (`net8.0-windows10.0.19041.0`) — native Windows shell
- **GongSolutions.Wpf.DragDrop** — drag-reorder + external file drop
- **Microsoft.Extensions.Hosting** — DI container + app lifecycle
- **xUnit + Moq** — testing

## Status

**v1.0.0** — first stable release. Full workflow ships: pin / launch / reorder / drag-drop / auto-hide. Multi-monitor selection is on the roadmap.

## License

MIT — see [LICENSE](DockXI/LICENSE).
