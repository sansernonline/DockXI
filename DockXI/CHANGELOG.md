# Changelog

All notable changes to DockXI are documented here.  
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

---

## [1.0.0] — 2026-09-16 (planned)

### Added

**Dock core**
- Borderless, always-on-top WinUI 3 window with Mica (Win 11) / Acrylic (Win 10 22H2) backdrop
- Automatic light/dark mode and accent color sync via `UISettings.ColorValuesChanged` + registry watch

**Pinning**
- Pin apps, folders, and URLs via drag-and-drop from Explorer or right-click → Add
- Broken-target badge for missing files/folders (warning overlay)
- Duplicate-pin prevention with inline InfoBar
- First-run onboarding tip (disappears after first pin)
- 50-item limit with user-facing dialog

**Icon extraction**
- Real shell icons via `IShellItemImageFactory` (primary) and `SHGetFileInfo` (fallback)
- `.lnk` shortcut resolution via `IShellLinkW`
- Per-DPI icon buckets (100 % / 200 %) with in-memory LRU cache
- Favicon fetch for URL pins (best-effort, 5 s timeout, generic fallback)

**Reorder**
- Drag-to-reorder with 150 ms hold threshold and smooth implicit `Translation` animation
- Cancel via Escape or cursor > 80 px outside dock
- Drag-off-dock unpin with confirmation dialog
- Order persists across restarts

**Running indicator**
- 1 Hz process snapshot; accent-coloured dot appears within 1 s of launch
- Disappears within 3 s of process exit
- `AutomationProperties.Name = "Running"` for screen readers

**Hover zoom** (US-005)
- `ExpressionAnimation` per-icon scale on the DWM compositor thread (60 fps)
- Low / Medium / High magnification presets (1.4× / 1.8× / 2.2×)
- Blocked during slide transitions; disabled when `UISettings.AnimationsEnabled = false`

**Auto-hide** (US-006)
- 500 ms debounce slide-out; 100 ms cursor-edge poll triggers slide-in
- Working-area reservation (`SPI_SETWORKAREA`) when auto-hide is OFF
- DWM cloaking (`DWMWA_CLOAK`) on fullscreen detection — no taskbar flicker

**Multi-monitor** (US-011)
- `DisplayAreaWatcher` tracks connect/disconnect events
- Automatic migration to primary monitor on disconnect; auto-restore on reconnect
- Per-monitor DPI via `GetDpiForWindow` + `GetDpiForMonitor`
- "Display" submenu in context menu for manual monitor selection

**Dock position** (US-007)
- Top / Bottom / Left / Right via context menu or Settings
- Vertical icon layout for Left/Right edges; working area updated immediately

**Settings dialog** (M6)
- Icon size slider (32 – 96 dp, live apply)
- Position, auto-hide, zoom, auto-start toggles
- Config file path with "Open folder" link

**Auto-start** (US-012 / M6)
- `StartupTask` API (MSIX-native); default OFF

**Persistence** (US-009)
- Debounced (250 ms) atomic JSON write (`config.json.tmp` → `MoveFileEx`)
- Corrupt-config recovery to `config.json.corrupt-<timestamp>` + defaults
- Schema versioning for future migrations

**Keyboard** (NFR-A11Y-02)
- Delete key on focused tile → unpin confirm dialog

**Diagnostics**
- Rolling file log in `LocalState\Logs\DockXI-YYYYMMDD.log`
- "Open log folder" in context menu

### Packaging
- MSIX bundle (x64); `StartupTask` disabled by default
- `runFullTrust` + `internetClient` capabilities

---

## [Unreleased]

See `docs/09-project-plan.md` for upcoming milestones.
