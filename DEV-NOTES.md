# DockXI — Dev Notes

Updated: 2026-09-27. Working knowledge captured from development sessions — read this before touching auto-hide, overflow scrolling, or the tray icon.

Project path: `C:\_DATA\Personal\Work\_KK\DockXI - Floating Dock` (moved out of OneDrive ~Sep 2026).
Run: `dotnet run --project src\DockXI.UI` · Distributable: `dotnet publish src\DockXI.UI -c Release` → self-contained single-file exe under `bin\x64\Publish\`.

## Auto-hide architecture (MainDockWindow.xaml.cs)

The hidden "pill" is NOT a separate UI — it's the dock itself scaled via `PlateScale` (ScaleTransform on DockPlate).

| Tunable | Value | Meaning |
|---------|-------|---------|
| `AutoHidePeekPx` | 5 | pill thickness (perpendicular to edge) |
| `AutoHidePillLongPx` | 120 | pill length along the dock axis |
| `AutoHideShowMs` / `AutoHideHideMs` | 720 | total show/hide duration |
| `AutoHideCooldownMs` | 750 | min time between toggles — MUST stay ≥ animation duration |
| `PillPhaseShow` / `PillPhaseHide` | 0.40 / 0.60 | phase split (locals in `AnimateAutoHide`) |

Animation is two SEQUENCED phases (pill↔strip flush at the edge, then slide in/out) so SHOW reads as "slide up from the edge" — deliberately not zoom-out-of-pill. All tracks are `Phased()` keyframes that capture current values, so interrupted transitions hand off smoothly; adaptive boundaries skip already-satisfied phases. `[autohide]` diagnostic lines go to `logs\activity.log`.

## Overflow scrolling + no pin cap

`PinnedItemRepository` has NO item cap (removed Sep 2026; unit test asserts 200 pins OK). The dock's long axis clamps to work area − 40 px (`OverflowMarginPx`); overflow scrolls via `TilesScroller` (hidden-scrollbar ScrollViewer; wheel mapped onto the long axis in `TilesScroller_PreviewMouseWheel`).

## Hard-won gotchas — do not re-learn these

- **`Window.MaxWidth/MaxHeight` are NOT reliably enforced under `SizeToContent`.** The overflow clamp must live on the ScrollViewer (`TilesScroller.MaxWidth/MaxHeight`, set per edge in `PositionAtScreenEdge`). Window-level Max appeared to work on Left/Right only because Win32's max-track-HEIGHT clamped the HWND; max-track-WIDTH derives from the virtual screen, so Top/Bottom never clamped → viewport == extent → wheel dead.
- **H.NotifyIcon.Wpf v2: a `TaskbarIcon` created in code needs `ForceCreate()`** or no tray icon appears at all (not even in the taskbar overflow). Done in `TrayIconManager` with `enablesEfficiencyMode: false` so the icon can't auto-hide.
- **Mouse events don't fire during an OLE drag.** Drag-to-pin onto a hidden dock works only via drag events: on the peek pill (`Window_DragOver`/`DockPlate_DragEnter` → `RevealForDrag()`) and on the reveal-zone window (AllowDrop + DragEnter/DragOver → PointerEntered, 200 ms re-ping to outlive the reveal cooldown). Abandoned drags re-hide via the hide timer's GetCursorPos poll — never rely on MouseLeave during a drag.
- `TreatWarningsAsErrors` is on — unused locals/fields or nullable slips fail the build.
- Windows remembers tray-icon visibility PER EXE PATH — Debug/Release/Publish count as different apps in Settings → Taskbar → Other system tray icons.

## Context-menu conventions

`DockMenu_Opened` resolves `_menuContextTile` (tile under the right-click; null on empty plate) and `_menuOpenPoint`. The Add file/folder pickers anchor `InitialDirectory` to the clicked tile's folder (folder pin → itself, file/app pin → parent), else the nearest tile along the dock axis (`GetPickerInitialDirectory`), skipping separators/URLs/broken paths.

## Docs to keep in sync when features change

`README.md` (root — features + build-outputs table) · `DockXI/README.md` (dev guide — UI Tunable Constants, Known Gotchas) · `DockXI/CHANGELOG.md` (`[Unreleased]`).
