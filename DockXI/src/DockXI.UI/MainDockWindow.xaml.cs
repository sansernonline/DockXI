using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DockXI.Contracts;
using GongSolutions.Wpf.DragDrop;
using Microsoft.Win32;

namespace DockXI.UI;

public partial class MainDockWindow : Window, INotifyPropertyChanged
{
    private const double DropGapPx = 10.0;
    private const int    GapAnimMs = 260;

    // ------------------------------------------------------------------------
    // Activity log — writes to <repo>/DockXI/logs/activity.log. Records
    // meaningful user-facing events (pin, unpin, launch, position change)
    // for post-mortem troubleshooting. Rotates at 1 MB to avoid unbounded
    // growth.
    // ------------------------------------------------------------------------
    private const long MaxLogBytes = 1_048_576;
    private static readonly string ActivityLogFile = ResolveActivityLogFile();

    private static string ResolveActivityLogFile()
    {
        // Write to the SAME date-stamped log file that FileLoggerProvider uses,
        // so MainDockWindow.LogEvent + Microsoft.Extensions.Logging output land
        // in one chronologically-ordered file (no more split between
        // activity.log and DockXI-YYYYMMDD.log).
        var exeDir = System.IO.Path.GetDirectoryName(Environment.ProcessPath)
                     ?? System.IO.Path.GetDirectoryName(typeof(MainDockWindow).Assembly.Location)
                     ?? AppContext.BaseDirectory;
        return System.IO.Path.Combine(exeDir, "logs",
            $"DockXI-{DateTimeOffset.UtcNow:yyyyMMdd}.log");
    }

    internal static void LogEvent(string msg)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(ActivityLogFile);
            if (dir is not null) { System.IO.Directory.CreateDirectory(dir); }
            // Rotate: if file exceeds threshold, keep only the tail half so
            // recent activity stays readable without growing forever.
            if (System.IO.File.Exists(ActivityLogFile))
            {
                var info = new System.IO.FileInfo(ActivityLogFile);
                if (info.Length > MaxLogBytes)
                {
                    var keep = System.IO.File.ReadAllText(ActivityLogFile);
                    keep = keep[(keep.Length / 2)..];
                    var newlineIdx = keep.IndexOf('\n');
                    if (newlineIdx >= 0) { keep = keep[(newlineIdx + 1)..]; }
                    System.IO.File.WriteAllText(ActivityLogFile, keep);
                }
            }
            // Match FileLoggerProvider's line format so both sources read
            // uniformly when scanning the merged file:
            //   HH:mm:ss.fff [Level      ] Category: message
            System.IO.File.AppendAllText(ActivityLogFile,
                $"{DateTimeOffset.Now:HH:mm:ss.fff} [Information] DockXI.Activity: {msg}{Environment.NewLine}");
        }
        catch { /* swallow — logging must never crash the app */ }
    }

    private readonly IPinnedItemRepository _pinnedRepo;
    private readonly ILaunchService        _launchService;
    private readonly IIconExtractor        _iconExtractor;
    private readonly IShortcutResolver     _shortcutResolver;
    private readonly IDockConfigStore      _dockConfigStore;
    private readonly IRevealZoneHost       _revealZoneHost;
    private readonly IAutoStartService     _autoStart;

    // --- Auto-hide state ----------------------------------------------------
    // Show + hide share duration AND easing so the two animations are exact
    // mirrors: show = gradually expand + slide outward; hide = gradually
    // shrink + slide back to edge. Same curve played in opposite directions.
    // 720 ms total because the animation is now TWO sequenced phases
    // (pill↔strip at the edge, then slide in/out — see AnimateAutoHide).
    // The slide phase gets 60% ≈ 432 ms, which preserves the original
    // 480 ms slide pace; the pill phase takes the remaining ~288 ms.
    private const int    AutoHideShowMs        = 720;
    private const int    AutoHideHideMs        = 720;
    // (Auto-hide no longer slides off-screen — it shrinks to a pill at the
    //  edge. Pill dimensions are AutoHidePillThick / AutoHidePillLong below.)
    private const int    AutoHideDelayMs       = 150; // delay after mouse leaves before hiding
    // Minimum time the dock must remain in its new state before it can
    // toggle again. Prevents flicker when the cursor sits at the screen
    // edge where the reveal zone and a freshly-shown dock overlap.
    private const int    AutoHideCooldownMs    = 500;
    private DateTime _lastAutoHideToggle = DateTime.MinValue;
    private System.Windows.Threading.DispatcherTimer? _hideTimer;
    private bool _isAutoHidden;
    private bool _isDragInProgress;
    private double _shownLeft;
    private double _shownTop;
    private double _shownWidth;
    private double _shownHeight;
    private const double AutoHidePeekPx      = 5.0;   // visible strip thickness when hidden
    private const double AutoHidePeekOpacity = 1.0;   // strip stays fully visible so the user sees it BEFORE show animation kicks in
    // Length of the hidden pill ALONG the dock axis. The hidden state used to
    // be a full-length strip (long axis unscaled); shrinking the long axis too
    // collapses it to a short pill centred on the dock's position. Only
    // affects the HIDDEN visual — show always animates back to scale 1.0, so
    // the main (shown) dock is untouched. The invisible reveal zone still
    // spans the full dock length, so hover-to-reveal stays as easy to hit.
    private const double AutoHidePillLongPx  = 120.0;


    // Bound by ItemsControl.ItemsPanel/StackPanel.Orientation. Flipped to
    // Vertical when the dock sits on Left/Right edges.
    private Orientation _itemsOrientation = Orientation.Horizontal;
    public Orientation ItemsOrientation
    {
        get => _itemsOrientation;
        set
        {
            if (_itemsOrientation == value) { return; }
            _itemsOrientation = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ItemsOrientation)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TileWrapOrientation)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlatePadding)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SeparatorOuterWidth)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SeparatorOuterHeight)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SeparatorLineWidth)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SeparatorLineHeight)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TileMargin)));
        }
    }

    // Separator visual dimensions, swapped so the divider always runs
    // PERPENDICULAR to the dock's main axis: vertical line for Top/Bottom,
    // horizontal line for Left/Right. Cross-axis size is kept slightly
    // SHORTER than an icon tile (which renders at 40 px + 2 px padding +
    // 4 px margin = ~52 px) so the dock height/width doesn't grow when a
    // separator is added.
    public double SeparatorOuterWidth  => _itemsOrientation == Orientation.Horizontal ? 8  : 40;
    public double SeparatorOuterHeight => _itemsOrientation == Orientation.Horizontal ? 40 : 8;
    public double SeparatorLineWidth   => _itemsOrientation == Orientation.Horizontal ? 1  : 28;
    public double SeparatorLineHeight  => _itemsOrientation == Orientation.Horizontal ? 28 : 1;

    // Inverse of ItemsOrientation — controls the layout INSIDE each tile so
    // the running-dot sits below the icon on a horizontal dock and beside it
    // on a vertical dock (per the mockup's "running dot → inward" spec).
    public Orientation TileWrapOrientation =>
        _itemsOrientation == Orientation.Horizontal ? Orientation.Vertical : Orientation.Horizontal;

    // Right-dock needs RightToLeft so the running-dot ends up between icon and
    // screen interior. Top/Bottom/Left keep LeftToRight.
    private FlowDirection _tileFlowDirection = FlowDirection.LeftToRight;
    public FlowDirection TileFlowDirection
    {
        get => _tileFlowDirection;
        set
        {
            if (_tileFlowDirection == value) { return; }
            _tileFlowDirection = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TileFlowDirection)));
        }
    }

    // Plate padding — asymmetric per edge so the icon sits AWAY from the
    // dock-interior side (Top → top, Bottom → bottom, etc.). The extra room
    // on the opposite side absorbs the hover-lift bounce without the icon
    // poking past the plate edge.
    public Thickness PlatePadding => new Thickness(4);   // 4 px symmetric — all sides equal
    // Tile margin: spacing ALONG the dock axis (between tiles); 0 across the
    // axis so icons sit flush against the plate edges → no asymmetric gap.
    public Thickness TileMargin => _itemsOrientation == Orientation.Horizontal
        ? new Thickness(2, 0, 2, 0)
        : new Thickness(0, 2, 0, 2);

    // Dock starts as a 52×52 square when empty; long axis grows as items pin.
    // Short axis is naturally bounded by tile thickness (>52) so MinWidth/Height
    // only takes effect in the empty state.
    public double DockMinWidth  { get; } = 52.0;
    public double DockMinHeight { get; } = 52.0;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<PinnedItemViewModel> PinnedItems { get; } = new();
    public IDragSource DragHandler { get; }
    public IDropTarget DropHandler { get; }

    public MainDockWindow(
        IPinnedItemRepository pinnedRepo,
        ILaunchService        launchService,
        IIconExtractor        iconExtractor,
        IShortcutResolver     shortcutResolver,
        IDockConfigStore      dockConfigStore,
        IRevealZoneHost       revealZoneHost,
        IAutoStartService     autoStart)
    {
        _pinnedRepo       = pinnedRepo;
        _launchService    = launchService;
        _iconExtractor    = iconExtractor;
        _shortcutResolver = shortcutResolver;
        _dockConfigStore  = dockConfigStore;
        _revealZoneHost   = revealZoneHost;
        _autoStart        = autoStart;

        // CRITICAL: assign drag/drop handlers BEFORE InitializeComponent so the
        // XAML binding {Binding Path=DragHandler} evaluates to our subclass,
        // not null (which makes gong silently fall back to its default handler
        // — that's why all our Trace overrides never fired).
        DragHandler = new TrackingDragSource(this);
        DropHandler = new GapDropHandler(this);

        InitializeComponent();

        foreach (var item in _pinnedRepo.Items)
        {
            CreateViewModel(item);
        }

        _pinnedRepo.ItemAdded   += OnItemAdded;
        _pinnedRepo.ItemRemoved += OnItemRemoved;
        _launchService.ProcessSnapshotUpdated += OnProcessSnapshotUpdated;

        _revealZoneHost.PointerEntered += OnRevealZonePointerEntered;
        MouseEnter += OnDockMouseEnter;
        MouseLeave += OnDockMouseLeave;

        // Re-assert HWND_TOPMOST periodically so Show Desktop (Win+D) or
        // any other Z-order shuffle from the shell can't push the dock
        // behind the taskbar. Cheap (a single SetWindowPos call) and
        // invisible to the user.
        var topmostTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(300),
        };
        topmostTimer.Tick += (_, _) =>
        {
            // Pause re-asserting topmost while a context menu is open — the
            // SetWindowPos call can dismiss the popup mid-selection.
            if (_isContextMenuOpen) { return; }
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
        };
        topmostTimer.Start();
    }

    // --- Startup / teardown -------------------------------------------------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc_MinMax);
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc_DropFiles);
        DragAcceptFiles(hwnd, true);

        // The HWND was already sized to the Win32 minimum (~132×38) before
        // the hook attached. Toggle SizeToContent off→on to force WPF to
        // re-measure with the new (1×1) MinTrackSize constraint, then we
        // shrink to the actual content size.
        var current = SizeToContent;
        SizeToContent = SizeToContent.Manual;
        SizeToContent = current;

        // UIPI bypass — allow drop messages from lower-IL (User) processes
        // when DockXI runs as Administrator. Without this, dragging files
        // from Explorer (User IL) to DockXI (Admin IL) is silently blocked
        // by Windows security and no drop event ever fires.
        AllowDropFromLowerIntegrityLevel(hwnd);
    }

    private const uint WM_DROPFILES        = 0x0233;
    private const uint WM_COPYDATA         = 0x004A;
    private const uint WM_COPYGLOBALDATA   = 0x0049;
    private const uint MSGFLT_ALLOW        = 1;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeWindowMessageFilterEx(
        IntPtr hWnd, uint msg, uint action, IntPtr changeFilterStruct);

    private static void AllowDropFromLowerIntegrityLevel(IntPtr hwnd)
    {
        ChangeWindowMessageFilterEx(hwnd, WM_DROPFILES,      MSGFLT_ALLOW, IntPtr.Zero);
        ChangeWindowMessageFilterEx(hwnd, WM_COPYDATA,       MSGFLT_ALLOW, IntPtr.Zero);
        ChangeWindowMessageFilterEx(hwnd, WM_COPYGLOBALDATA, MSGFLT_ALLOW, IntPtr.Zero);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var isAdmin = new System.Security.Principal.WindowsPrincipal(
            System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        var asm = Assembly.GetExecutingAssembly();
        var version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? asm.GetName().Version?.ToString() ?? "?";
        var plus = version.IndexOf('+');
        if (plus > 0) { version = version[..plus]; }
        LogEvent($"App started v{version}, position={_dockConfigStore.Current.Position}, admin={isAdmin}, pinned={PinnedItems.Count}");
        var hwnd = new WindowInteropHelper(this).Handle;
        ApplyToolWindowStyles(hwnd);
        ApplyDarkTitleBar(hwnd);
        PositionAtScreenEdge(_dockConfigStore.Current.Position);
        HideGongBar();

        // Re-center dock whenever its size changes (pin / unpin grows / shrinks
        // the dock along the long axis — without this hook the dock keeps its
        // old Left/Top anchor and drifts off-centre as items accumulate).
        SizeChanged += OnDockSizeChanged;

        var dpi = GetDpi();
        foreach (var vm in PinnedItems)
        {
            _ = vm.LoadIconAsync(_iconExtractor, dpi);
        }

        // If auto-hide was on at last shutdown, slide off-screen immediately
        // so the user only sees a thin peek strip until they hover.
        if (_dockConfigStore.Current.AutoHide)
        {
            ApplyAutoHide(true, animate: false);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        LogEvent("App exiting");
        _pinnedRepo.ItemAdded   -= OnItemAdded;
        _pinnedRepo.ItemRemoved -= OnItemRemoved;
        _launchService.ProcessSnapshotUpdated -= OnProcessSnapshotUpdated;
        _revealZoneHost.PointerEntered -= OnRevealZonePointerEntered;
        MouseEnter -= OnDockMouseEnter;
        MouseLeave -= OnDockMouseLeave;
        _hideTimer?.Stop();
        _revealZoneHost.Hide();
        base.OnClosed(e);
    }

    // --- Repository <-> ViewModel sync --------------------------------------

    private PinnedItemViewModel CreateViewModel(PinnedItem item)
    {
        var vm = new PinnedItemViewModel(item)
        {
            IsRunning = _launchService.IsProcessRunning(item),
        };
        // Honor the repo's insertion index — it sets item.SortOrder to the
        // requested insertion slot, so "Add separator next to the right-
        // clicked tile" actually lands the new tile in the right position.
        // Clamp into PinnedItems bounds in case of races.
        var insertAt = Math.Clamp(item.SortOrder, 0, PinnedItems.Count);
        PinnedItems.Insert(insertAt, vm);
        return vm;
    }

    private void OnItemAdded(object? sender, PinnedItemEventArgs e)
    {
        LogEvent($"Pin: {e.Item.DisplayName} → {e.Item.TargetPath}");
        Dispatcher.Invoke(() =>
        {
            var vm = CreateViewModel(e.Item);
            _ = vm.LoadIconAsync(_iconExtractor, GetDpi());
        });
    }

    private void OnItemRemoved(object? sender, PinnedItemEventArgs e)
    {
        LogEvent($"Unpin: {e.Item.DisplayName}");
        Dispatcher.Invoke(() =>
        {
            var vm = PinnedItems.FirstOrDefault(v => v.Id == e.Item.Id);
            if (vm is null) { return; }

            // Bounce-out: scale 1 → 0 with smooth ease, fade opacity in parallel,
            // then actually remove from the collection. If we can't find the
            // container (virtualization edge-case), remove immediately.
            var scale = GetTileScale(vm);
            if (scale is null)
            {
                PinnedItems.Remove(vm);
                return;
            }

            // Slower (260ms) + scale to 0.6 (not 0) so the icon shrinks subtly
            // while fading rather than snapping out of existence.
            var dur = TimeSpan.FromMilliseconds(260);
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var scaleAnim = new DoubleAnimation
            {
                To = 0.6,
                Duration = dur,
                EasingFunction = ease,
                FillBehavior = FillBehavior.HoldEnd,
            };
            scaleAnim.Completed += (_, _) => PinnedItems.Remove(vm);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);

            // Fade opacity on the container so the icon dissolves while shrinking.
            var idx = PinnedItems.IndexOf(vm);
            if (TilesHost.ItemContainerGenerator.ContainerFromIndex(idx) is ContentPresenter cp
                && FindDescendant<Grid>(cp, "TileSlot") is { } slot)
            {
                var fade = new DoubleAnimation
                {
                    To = 0.0,
                    Duration = dur,
                    EasingFunction = ease,
                    FillBehavior = FillBehavior.HoldEnd,
                };
                slot.BeginAnimation(UIElement.OpacityProperty, fade);
            }
        });
    }

    private void OnProcessSnapshotUpdated(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            foreach (var vm in PinnedItems)
            {
                vm.IsRunning = _launchService.IsProcessRunning(vm.Model);
            }
        });
    }

    internal void SyncReorderToRepository()
    {
        try
        {
            var orderedIds = PinnedItems.Select(vm => vm.Id).ToList();
            _pinnedRepo.Reorder(orderedIds);
            LogEvent($"Reorder: [{string.Join(", ", PinnedItems.Select(v => v.DisplayName))}]");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DockXI] Reorder sync failed: {ex.Message}");
        }
    }

    // --- Tile click ---------------------------------------------------------

    private async void Tile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PinnedItemViewModel vm })
        {
            var ok = await _launchService.LaunchAsync(vm.Model);
            LogEvent(ok
                ? $"Launch: {vm.DisplayName}"
                : $"Launch failed: {vm.DisplayName} → {vm.Model.TargetPath}");
            if (!ok)
            {
                MessageBox.Show($"Failed to launch \"{vm.DisplayName}\".", "DockXI",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }


    // Center the hover tooltip and adapt placement to the dock's edge — the
    // label always points "into" the screen so it never overlaps the taskbar
    // or another monitor. Bottom → above tile, Top → below tile, Left → right
    // of tile, Right → left of tile.
    private void Tile_ToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is not FrameworkElement fe)             { return; }
        if (fe.ToolTip is not ToolTip tt)                  { return; }

        var edge = _dockConfigStore.Current.Position;
        // Force LTR so the placement math below isn't flipped on Right dock
        // (whose StackPanel uses FlowDirection=RightToLeft for the dot side).
        tt.FlowDirection = FlowDirection.LeftToRight;
        tt.Placement = PlacementMode.Custom;
        tt.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
        {
            // 5 dp clearance OUTSIDE the plate edge. The plate-edge offset from
            // the button is just the PlatePadding on that side.
            const double gap = 10.0;
            var pad = PlatePadding;
            double x, y;
            switch (edge)
            {
                case DockEdge.Top:
                    // tooltip below icon → exits through plate bottom
                    x = (targetSize.Width - popupSize.Width) / 2.0;
                    y = targetSize.Height + pad.Bottom + gap + 10.0;
                    break;
                case DockEdge.Left:
                    // tooltip right of icon → exits through plate right
                    x = targetSize.Width + pad.Right + gap + 10.0;
                    y = (targetSize.Height - popupSize.Height) / 2.0;
                    break;
                case DockEdge.Right:
                    // tooltip left of icon → exits through plate left.
                    // Extra -30 nudge: empirically the LTR override + RTL-parent
                    // measurement leaves a gap on the right; -30 cancels it.
                    x = -popupSize.Width - pad.Left - gap - 100.0;
                    y = (targetSize.Height - popupSize.Height) / 2.0;
                    break;
                case DockEdge.Bottom:
                default:
                    // tooltip above icon → exits through plate top
                    x = (targetSize.Width - popupSize.Width) / 2.0;
                    y = -popupSize.Height - pad.Top - gap - 10.0;
                    break;
            }
            return new[] { new CustomPopupPlacement(new Point(x, y), PopupPrimaryAxis.None) };
        };
    }

    // --- Layout -------------------------------------------------------------

    // Re-entrance guard + last-size memo to stop reposition loops:
    // - Guard: PositionAtScreenEdge → UpdateLayout() can re-trigger SizeChanged,
    //   which would otherwise call this handler again before the first call
    //   returns.
    // - Threshold: WPF can emit SizeChanged with sub-pixel deltas (≤ 0.5 px)
    //   during hover animations, push-aside, or DPI-rounding passes. Acting on
    //   every one of those repositions the window mid-frame, which the user
    //   sees as "shake" while hovering tiles. We only reposition when the size
    //   actually changes by more than 1 pixel — i.e. real pin/unpin events.
    private bool _isRepositioning;
    private bool _isAutoHideAnimating;
    private double _lastRepositionedW;
    private double _lastRepositionedH;

    private void OnDockSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Skip while:
        // - the dock is auto-hidden (peek strip — keep it pinned to the edge);
        // - we're already repositioning (re-entrance guard, see field comment);
        // - the show/hide animation is mid-flight. The animation drives Left,
        //   Top, Width, Height all together; letting PositionAtScreenEdge fire
        //   from this handler in the middle would yank Left/Top to the centred
        //   value and make the show look like a jump instead of a slide.
        if (_isAutoHidden || _isRepositioning || _isAutoHideAnimating) { return; }

        var dw = Math.Abs(e.NewSize.Width  - _lastRepositionedW);
        var dh = Math.Abs(e.NewSize.Height - _lastRepositionedH);
        if (dw < 1.0 && dh < 1.0) { return; }

        _isRepositioning = true;
        try
        {
            PositionAtScreenEdge(_dockConfigStore.Current.Position);
            _lastRepositionedW = ActualWidth;
            _lastRepositionedH = ActualHeight;
        }
        finally { _isRepositioning = false; }
    }

    private void PositionAtScreenEdge(DockEdge edge) =>
        PositionAtScreenEdge(edge, followCursor: false);

    private void PositionAtScreenEdge(DockEdge edge, bool followCursor)
    {
        // Flip the items panel BEFORE measuring so SizeToContent picks the new
        // orientation. Top/Bottom → row, Left/Right → column.
        ItemsOrientation = edge is DockEdge.Left or DockEdge.Right
            ? Orientation.Vertical
            : Orientation.Horizontal;
        TileFlowDirection = edge == DockEdge.Right
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;

        // Anchor the dock-plate ScaleTransform to the SCREEN-FACING side so
        // hide-shrink collapses the WHOLE dock (background + items together)
        // toward the edge it's going to hide behind.
        DockPlate.RenderTransformOrigin = edge switch
        {
            DockEdge.Bottom => new System.Windows.Point(0.5, 1.0),
            DockEdge.Top    => new System.Windows.Point(0.5, 0.0),
            DockEdge.Left   => new System.Windows.Point(0.0, 0.5),
            DockEdge.Right  => new System.Windows.Point(1.0, 0.5),
            _               => new System.Windows.Point(0.5, 0.5),
        };

        // Clear any in-flight auto-hide animation so a direct Left/Top
        // assignment below isn't overridden by an animation hold-end.
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty,  null);

        // Multi-monitor: anchor to the screen the dock is CURRENTLY on (or the
        // screen under the cursor if dock isn't placed yet). Falls back to the
        // primary monitor. SystemParameters.WorkArea alone would always anchor
        // to PRIMARY — that's wrong on multi-monitor setups: the "right edge
        // of primary" sits between the monitors, not at the visible right edge
        // of whichever monitor the user is using.
        var (work, fullBottom) = GetTargetScreenBounds(followCursor);
        var w            = work;
        var screenBottom = fullBottom;

        // --- Overflow clamp --------------------------------------------------
        // The dock's LONG axis may not outgrow the monitor: clamp it to the
        // work-area length minus a margin on both ends (the EdgeGap on each
        // side). When the tile row exceeds this, TilesScroller (XAML) scrolls
        // the overflow — TilesScroller_PreviewMouseWheel maps the wheel onto
        // the dock's long axis. The scrollable direction is also swapped per
        // edge here so the inactive axis can never steal wheel events.
        //
        // CRITICAL: the clamp lives on the SCROLLVIEWER, not the Window.
        // Window.MaxWidth/MaxHeight are NOT reliably enforced while
        // SizeToContent is active — the window kept sizing to the full tile
        // row. (Left/Right docks only appeared to work because Win32's
        // default max track HEIGHT ≈ screen height clamped the HWND; the max
        // track WIDTH comes from the virtual screen, so Top/Bottom docks
        // never got clamped → viewport == extent → ScrollableWidth == 0 →
        // wheel dead.) An element-level Max is applied unconditionally in
        // measure, the SizeToContent window then shrinks to the clamped
        // content, and the viewport becomes smaller than the extent so the
        // wheel handler has something to scroll on every edge.
        //
        // Clamping happens BEFORE UpdateLayout() so ActualWidth/ActualHeight
        // below report the CLAMPED size — centring, auto-hide
        // (_shownWidth/_shownHeight → pill scale) and the reveal zone all
        // stay consistent with what's actually on screen.
        const double OverflowMarginPx = 40.0;    // EdgeGapPx on both ends
        // Chrome around the scroller inside the window: DockPlate border
        // (1 px × 2) + PlatePadding (4 px × 2) — subtracted so the WINDOW
        // (scroller + chrome) lands on work-area − OverflowMarginPx.
        const double PlateChromePx = 10.0;
        if (edge is DockEdge.Left or DockEdge.Right)
        {
            TilesScroller.MaxWidth  = double.PositiveInfinity;
            TilesScroller.MaxHeight = Math.Max(DockMinHeight,
                w.Height - OverflowMarginPx - PlateChromePx);
            TilesScroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            TilesScroller.VerticalScrollBarVisibility   = ScrollBarVisibility.Hidden;
        }
        else
        {
            TilesScroller.MaxWidth  = Math.Max(DockMinWidth,
                w.Width - OverflowMarginPx - PlateChromePx);
            TilesScroller.MaxHeight = double.PositiveInfinity;
            TilesScroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
            TilesScroller.VerticalScrollBarVisibility   = ScrollBarVisibility.Disabled;
        }

        UpdateLayout();
        const double EdgeGapPx = 20.0;           // gap between dock and screen edge
        switch (edge)
        {
            case DockEdge.Bottom:
                Left = w.Left + (w.Width - ActualWidth) / 2;
                Top  = screenBottom - ActualHeight - EdgeGapPx;
                break;
            case DockEdge.Top:
                Left = w.Left + (w.Width - ActualWidth) / 2;
                Top  = w.Top + EdgeGapPx;
                break;
            case DockEdge.Left:
                Left = w.Left + EdgeGapPx;
                Top  = w.Top + (w.Height - ActualHeight) / 2;
                break;
            case DockEdge.Right:
                Left = w.Right - ActualWidth - EdgeGapPx;
                Top  = w.Top + (w.Height - ActualHeight) / 2;
                break;
        }
    }

    private int GetDpi()
    {
        var m11 = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11;
        return (int)((m11 ?? 1.0) * 96.0);
    }

    // ---- Multi-monitor: pick the screen the dock should anchor to ----------
    // Strategy: prefer the screen containing the dock's CURRENT centre point
    // (so the dock stays where the user dragged it). Fallback to the screen
    // under the cursor (so when the user picks Position from a context menu on
    // monitor B, the dock jumps to monitor B). Final fallback is primary.
    // Returns work-area and "screen bottom" both in DIPs so callers can stay
    // unit-consistent with the rest of the WPF layout code.

    // Multi-monitor Win32 interop. POINT + GetCursorPos are declared further
    // down with the cursor-polling helpers — we reuse those so we don't dup
    // declarations.
    private const int MONITOR_DEFAULTTONEAREST = 2;
    [StructLayout(LayoutKind.Sequential)] private struct RECT_M { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT_M rcMonitor;
        public RECT_M rcWork;
        public uint dwFlags;
    }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, int flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private (Rect work, double screenBottom) GetTargetScreenBounds(bool followCursor = false)
    {
        IntPtr hMon = IntPtr.Zero;

        // When the user explicitly invokes Position from the menu, "follow
        // cursor" gives them an obvious way to move the dock between monitors:
        // they hover the target screen first, then pick the edge. For passive
        // calls (size-changed, initial load) we prefer the dock's own monitor
        // so the dock doesn't randomly jump to whichever screen the cursor is
        // currently on.
        if (followCursor && GetCursorPos(out var cursor))
        {
            hMon = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
        }

        if (hMon == IntPtr.Zero)
        {
            // 1) Try the monitor containing the dock window itself.
            var hwnd = new WindowInteropHelper(this).Handle;
            hMon = hwnd != IntPtr.Zero
                ? MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST)
                : IntPtr.Zero;
        }

        // 2) Fallback: monitor under the cursor (matches user intent when they
        //    pick Position from a context menu on a specific screen).
        if (hMon == IntPtr.Zero && GetCursorPos(out var cursor2))
        {
            hMon = MonitorFromPoint(cursor2, MONITOR_DEFAULTTONEAREST);
        }

        if (hMon != IntPtr.Zero)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMon, ref mi))
            {
                // GetMonitorInfo returns DEVICE pixels. Convert to DIPs using
                // the current visual's transform-to-device matrix.
                var src = PresentationSource.FromVisual(this);
                var m11 = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                var m22 = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
                var work = new Rect(
                    mi.rcWork.Left   / m11,
                    mi.rcWork.Top    / m22,
                    (mi.rcWork.Right - mi.rcWork.Left) / m11,
                    (mi.rcWork.Bottom - mi.rcWork.Top) / m22);
                var fullBottom = mi.rcMonitor.Bottom / m22;
                return (work, fullBottom);
            }
        }

        // 3) Final fallback: primary monitor (legacy behaviour).
        return (SystemParameters.WorkArea, SystemParameters.PrimaryScreenHeight);
    }

    private void HideGongBar()
    {
        var transparentPen = new Pen(Brushes.Transparent, 0);
        transparentPen.Freeze();
        GongSolutions.Wpf.DragDrop.DragDrop.SetDropTargetAdornerPen(TilesHost, transparentPen);
        GongSolutions.Wpf.DragDrop.DragDrop.SetDropTargetAdornerBrush(TilesHost, Brushes.Transparent);
    }

    // --- Drop gap (push-aside) ---------------------------------------------

    internal void ShowDropGap(int targetIndex, object? draggedData)
    {
        var sourceIdx = draggedData is PinnedItemViewModel vm
            ? PinnedItems.IndexOf(vm)
            : -1;

        var half = DropGapPx / 2.0;
        for (var i = 0; i < TilesHost.Items.Count; i++)
        {
            var tt = GetTileTranslate(i);
            if (tt is null) { continue; }

            double to;
            if      (i == sourceIdx)       { to =  0.0; }
            else if (i == targetIndex - 1) { to = -half; }
            else if (i == targetIndex)     { to =  half; }
            else                           { to =  0.0; }

            AnimateAlongAxis(tt, to);
        }
    }

    internal void ResetDropGap()
    {
        for (var i = 0; i < TilesHost.Items.Count; i++)
        {
            var tt = GetTileTranslate(i);
            if (tt is null) { continue; }
            AnimateAlongAxis(tt, 0.0);
        }
    }

    // Pick X-axis for horizontal dock (Top/Bottom) and Y-axis for vertical
    // dock (Left/Right) so push-aside slides along the dock's flow direction
    // instead of always horizontally.
    private void AnimateAlongAxis(TranslateTransform tt, double to)
    {
        var prop = _itemsOrientation == Orientation.Horizontal
            ? TranslateTransform.XProperty
            : TranslateTransform.YProperty;
        // Stop any previously-running animation on the OTHER axis so the tile
        // doesn't get stuck offset perpendicular to the dock after orientation
        // changes via the Position menu.
        var otherProp = _itemsOrientation == Orientation.Horizontal
            ? TranslateTransform.YProperty
            : TranslateTransform.XProperty;
        tt.BeginAnimation(otherProp, null);
        if (_itemsOrientation == Orientation.Horizontal) { tt.Y = 0; } else { tt.X = 0; }

        var anim = new DoubleAnimation
        {
            To             = to,
            Duration       = TimeSpan.FromMilliseconds(GapAnimMs),
            EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior   = FillBehavior.HoldEnd,
        };
        tt.BeginAnimation(prop, anim);
    }

    // --- Insert bar ---------------------------------------------------------

    internal void ShowInsertBar(int targetIndex)
    {
        var offset = ComputeInsertOffset(targetIndex);
        if (offset is null) { return; }

        // Orientation-aware geometry: vertical bar (1px wide, stretched in Y)
        // for horizontal dock; horizontal bar (1px tall, stretched in X) for
        // vertical dock. Position via the matching axis on the translate.
        if (_itemsOrientation == Orientation.Horizontal)
        {
            InsertBar.Width               = 1;
            InsertBar.Height              = double.NaN;
            InsertBar.HorizontalAlignment = HorizontalAlignment.Left;
            InsertBar.VerticalAlignment   = VerticalAlignment.Stretch;
            InsertBarT.X = offset.Value;
            InsertBarT.Y = 0;
        }
        else
        {
            InsertBar.Width               = double.NaN;
            InsertBar.Height              = 1;
            InsertBar.HorizontalAlignment = HorizontalAlignment.Stretch;
            InsertBar.VerticalAlignment   = VerticalAlignment.Top;
            InsertBarT.X = 0;
            InsertBarT.Y = offset.Value;
        }
        InsertBar.Visibility = Visibility.Visible;
    }

    internal void HideInsertBar() => InsertBar.Visibility = Visibility.Collapsed;

    // Returns the offset (X for horizontal dock, Y for vertical dock) along
    // the dock's main axis where the 1px insert bar should appear.
    private double? ComputeInsertOffset(int targetIndex)
    {
        var horizontal = _itemsOrientation == Orientation.Horizontal;
        var count = TilesHost.Items.Count;
        if (count == 0)
        {
            // Empty dock: bar in the middle of the placeholder area.
            return (horizontal ? TilesHost.ActualWidth : TilesHost.ActualHeight) / 2.0;
        }

        double MainAxis(Point p) => horizontal ? p.X : p.Y;

        if (targetIndex <= 0)
        {
            var first = TilesHost.ItemContainerGenerator.ContainerFromIndex(0) as FrameworkElement;
            if (first is null) { return null; }
            return MainAxis(first.TransformToVisual(TilesHost).Transform(new Point(0, 0)));
        }
        if (targetIndex >= count)
        {
            var last = TilesHost.ItemContainerGenerator.ContainerFromIndex(count - 1) as FrameworkElement;
            if (last is null) { return null; }
            var tail = horizontal ? new Point(last.ActualWidth, 0) : new Point(0, last.ActualHeight);
            return MainAxis(last.TransformToVisual(TilesHost).Transform(tail));
        }
        var prev = TilesHost.ItemContainerGenerator.ContainerFromIndex(targetIndex - 1) as FrameworkElement;
        var curr = TilesHost.ItemContainerGenerator.ContainerFromIndex(targetIndex)     as FrameworkElement;
        if (prev is null || curr is null) { return null; }
        var prevTail = horizontal ? new Point(prev.ActualWidth, 0) : new Point(0, prev.ActualHeight);
        var r = MainAxis(prev.TransformToVisual(TilesHost).Transform(prevTail));
        var l = MainAxis(curr.TransformToVisual(TilesHost).Transform(new Point(0, 0)));
        return (r + l) / 2.0;
    }

    // --- Visual-tree helpers ------------------------------------------------

    private TranslateTransform? GetTileTranslate(int index)
    {
        if (TilesHost.ItemContainerGenerator.ContainerFromIndex(index) is not ContentPresenter cp)
        {
            return null;
        }
        var slot = FindDescendant<Grid>(cp, "TileSlot");
        // TileSlot now uses TransformGroup [ScaleTransform, TranslateTransform]
        // to support both bounce-in scale and push-aside translate.
        if (slot?.RenderTransform is TransformGroup tg)
        {
            return tg.Children.OfType<TranslateTransform>().FirstOrDefault();
        }
        return slot?.RenderTransform as TranslateTransform;
    }

    private ScaleTransform? GetTileScale(PinnedItemViewModel vm)
    {
        var idx = PinnedItems.IndexOf(vm);
        if (idx < 0) { return null; }
        if (TilesHost.ItemContainerGenerator.ContainerFromIndex(idx) is not ContentPresenter cp)
        {
            return null;
        }
        var slot = FindDescendant<Grid>(cp, "TileSlot");
        if (slot?.RenderTransform is TransformGroup tg)
        {
            return tg.Children.OfType<ScaleTransform>().FirstOrDefault();
        }
        return null;
    }

    private static T? FindDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t && t.Name == name) { return t; }
            var found = FindDescendant<T>(c, name);
            if (found is not null) { return found; }
        }
        return null;
    }

    // --- Pin helpers --------------------------------------------------------

    internal void PinFiles(string[] paths, int insertIndex)
    {
        var idx = Math.Clamp(insertIndex, 0, _pinnedRepo.Count);
        foreach (var raw in paths)
        {
            var resolved = _shortcutResolver.ResolveTargetPath(raw) ?? raw;
            if (_pinnedRepo.FindByTargetPath(resolved) is not null) { continue; }

            var kind = Directory.Exists(resolved) ? PinnedItemKind.Folder : PinnedItemKind.Application;
            var name = Path.GetFileNameWithoutExtension(resolved);
            if (string.IsNullOrWhiteSpace(name)) { name = resolved; }

            var item = new PinnedItem { TargetPath = resolved, DisplayName = name, Kind = kind };
            try
            {
                _pinnedRepo.Add(item, idx++);
            }
            catch (InvalidOperationException) { break; }
        }
    }

    // --- Window-level external-file drop (fallback) -----------------------
    // Catches drops on parts of the window outside the Border/ItemsControl.

    // Trace flag so Window_DragOver doesn't spam — only logs the first DragOver
    // per drag session (cleared in Window_Drop / DragLeave). Without this we'd
    // get ~60 log lines per second while the user holds a drag.
    private bool _loggedDragOverThisSession;

    // WPF suppresses normal tooltips while a drag is in progress, so open the
    // hovered tile's tooltip by hand: the user sees the folder/app name while
    // dragging a file across the dock. Tunnelling events never get swallowed
    // by the drag-drop library's handlers on TilesHost.
    private Button? _dragHoverTile;

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        var tile = TileButtonAt(e.GetPosition(TilesHost));
        if (ReferenceEquals(tile, _dragHoverTile)) { return; }
        CloseDragTooltip();
        if (tile?.ToolTip is not ToolTip tt) { return; }
        _dragHoverTile = tile;
        Tile_ToolTipOpening(tile, null!);
        tt.PlacementTarget = tile;
        tt.IsOpen = true;
    }

    private void Window_PreviewDragLeave(object sender, DragEventArgs e)
    {
        var p = e.GetPosition(this);
        if (p.X < 0 || p.Y < 0 || p.X > ActualWidth || p.Y > ActualHeight) { CloseDragTooltip(); }
    }

    private void Window_PreviewDrop(object sender, DragEventArgs e) => CloseDragTooltip();

    private void CloseDragTooltip()
    {
        if (_dragHoverTile?.ToolTip is ToolTip tt) { tt.IsOpen = false; }
        _dragHoverTile = null;
    }

    private Button? TileButtonAt(Point pt)
    {
        var hit = TilesHost.InputHitTest(pt) as DependencyObject;
        while (hit is not null && hit != TilesHost)
        {
            if (hit is Button b && b.DataContext is PinnedItemViewModel) { return b; }
            hit = System.Windows.Media.VisualTreeHelper.GetParent(hit);
        }
        return null;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (!_loggedDragOverThisSession)
        {
            _loggedDragOverThisSession = true;
            var hasFiles = e.Data.GetDataPresent(DataFormats.FileDrop);
            var isLocked = _dockConfigStore.Current.IsLocked;
            LogEvent($"[drag] Window_DragOver (first): hasFiles={hasFiles}, locked={isLocked}");
        }
        if (_dockConfigStore.Current.IsLocked)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
            // Drag-to-pin while auto-hidden: the drag is hovering the peek
            // pill — pop the dock out so the user can complete the drop.
            RevealForDrag();
        }
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        _loggedDragOverThisSession = false;
        var hasFiles = e.Data.GetDataPresent(DataFormats.FileDrop);
        var isLocked = _dockConfigStore.Current.IsLocked;
        LogEvent($"[drag] Window_Drop: hasFiles={hasFiles}, locked={isLocked}");
        if (isLocked) { return; }
        if (!hasFiles) { return; }
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            LogEvent("[drag] Window_Drop: FileDrop not string[]");
            return;
        }
        LogEvent($"[drag] Window_Drop: pinning {paths.Length} file(s)");
        if (paths.Length == 0) { return; }
        PinFiles(paths, PinnedItems.Count);
        e.Handled = true;
    }

    // --- External file drop (Border-level fallback for empty dock + padding) ---

    private void DockPlate_DragEnter(object sender, DragEventArgs e)
    {
        var hasFiles  = e.Data.GetDataPresent(DataFormats.FileDrop);
        var isLocked  = _dockConfigStore.Current.IsLocked;
        LogEvent($"[drag] DockPlate_DragEnter: hasFiles={hasFiles}, locked={isLocked}");
        if (isLocked)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        if (hasFiles)
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
            RevealForDrag();
        }
    }

    private void DockPlate_DragOver(object sender, DragEventArgs e)
    {
        if (_dockConfigStore.Current.IsLocked)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
            ShowInsertBar(PinnedItems.Count);
        }
    }

    private void DockPlate_DragLeave(object sender, DragEventArgs e)
    {
        HideInsertBar();
    }

    private void DockPlate_Drop(object sender, DragEventArgs e)
    {
        HideInsertBar();
        var hasFiles = e.Data.GetDataPresent(DataFormats.FileDrop);
        var isLocked = _dockConfigStore.Current.IsLocked;
        LogEvent($"[drag] DockPlate_Drop: hasFiles={hasFiles}, locked={isLocked}");
        if (isLocked) { return; }
        if (!hasFiles) { return; }
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            LogEvent($"[drag] DockPlate_Drop: not string[] - GetData returned {e.Data.GetData(DataFormats.FileDrop)?.GetType().Name ?? "null"}");
            return;
        }
        LogEvent($"[drag] DockPlate_Drop: pinning {paths.Length} file(s): {string.Join(", ", paths)}");
        if (paths.Length == 0) { return; }
        PinFiles(paths, PinnedItems.Count);
        e.Handled = true;
    }


    private void DeleteTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.DataContext is PinnedItemViewModel vm)
        {
            _pinnedRepo.Remove(vm.Id);
        }
    }

    // Delete key → remove the tile currently under the mouse cursor. Falls
    // back to no-op if the cursor isn't over a tile, so users don't
    // accidentally delete something else.
    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Delete) { return; }
        var hovered = TileUnderCursor();
        if (hovered is null) { return; }
        _pinnedRepo.Remove(hovered.Id);
        e.Handled = true;
    }

    private PinnedItemViewModel? TileUnderCursor()
    {
        var pt   = System.Windows.Input.Mouse.GetPosition(this);
        var hit  = InputHitTest(pt) as System.Windows.DependencyObject;
        while (hit is not null)
        {
            if (hit is System.Windows.FrameworkElement fe && fe.DataContext is PinnedItemViewModel vm)
            {
                return vm;
            }
            hit = System.Windows.Media.VisualTreeHelper.GetParent(hit);
        }
        return null;
    }

    // --- Context menu -------------------------------------------------------

    // True while ANY ContextMenu instance from this shared resource is open.
    // Auto-hide + topmost timers respect this so they don't close the menu
    // out from under the user mid-selection.
    private bool _isContextMenuOpen;

    // Tracks the tile under the right-click that opened the current context
    // menu, so PinSeparator_Click can insert the new separator NEXT TO it
    // rather than at the end of the dock. Null = menu opened from empty
    // plate (no tile context).
    private PinnedItemViewModel? _menuContextTile;
    // Where (in TilesHost coordinates) the dock context menu was opened —
    // used to find the tile NEAREST an empty-space right-click so the
    // Add file/folder pickers can anchor to "the neighbouring folder".
    private Point _menuOpenPoint;

    private void DockMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) { return; }
        _isContextMenuOpen = true;
        StartMenuPolling(menu);

        // Remove any dynamically-injected "Delete \"<name>\"" from a previous
        // open before re-evaluating the placement target.
        for (var i = menu.Items.Count - 1; i >= 0; i--)
        {
            if (menu.Items[i] is FrameworkElement fe && Equals(fe.Tag, "DeleteDynamic"))
            {
                menu.Items.RemoveAt(i);
            }
        }

        // Resolve the tile this menu was opened over — works for both Button
        // (icon tile) and Border (separator). PlacementTarget is the element
        // the right-click landed on. Walk up the visual tree for a
        // FrameworkElement whose DataContext is a PinnedItemViewModel.
        PinnedItemViewModel? vm = null;
        if (menu.PlacementTarget is DependencyObject target)
        {
            var cur = target;
            while (cur is not null)
            {
                if (cur is FrameworkElement el && el.DataContext is PinnedItemViewModel hit)
                {
                    vm = hit;
                    break;
                }
                cur = System.Windows.Media.VisualTreeHelper.GetParent(cur);
            }
        }
        _menuContextTile = vm;
        _menuOpenPoint   = Mouse.GetPosition(TilesHost);
        LogEvent($"[menu] Opened — PlacementTarget={menu.PlacementTarget?.GetType().Name ?? "null"}, contextTile={(vm is null ? "null" : vm.DisplayName + (vm.IsSeparator ? " (sep)" : ""))}");

        // If opened from a tile, inject a "Delete \"<name>\"" entry just
        // before the separator that precedes "About DockXI" — item-specific
        // action sits with the dock-settings group, visually separated from
        // About / Quit.
        if (vm is not null)
        {
            // Separators have no meaningful name, so use a generic label.
            var deleteLabel = vm.IsSeparator
                ? "Delete separator"
                : $"Delete \"{vm.DisplayName}\"";
            var delete = new MenuItem
            {
                Header      = deleteLabel,
                Style       = (Style)FindResource("DockMenuItemStyle"),
                DataContext = vm,
                Tag         = "DeleteDynamic",
            };
            delete.Click += DeleteTile_Click;

            var insertIdx = menu.Items.Count;
            for (var i = 0; i < menu.Items.Count; i++)
            {
                if (menu.Items[i] is MenuItem m && Equals(m.Header, "About DockXI"))
                {
                    // Insert just BEFORE the separator that precedes About,
                    // so structure becomes:
                    //   Auto-hide
                    //   ─── (new sep above Delete)
                    //   Delete "<name>"
                    //   ─── (original sep before About)
                    //   About
                    insertIdx = (i > 0 && menu.Items[i - 1] is Separator) ? i - 1 : i;
                    break;
                }
            }
            var sepAbove = new Separator
            {
                Style = (Style)FindResource("DockSeparatorStyle"),
                Tag   = "DeleteDynamic",
            };
            menu.Items.Insert(insertIdx,     sepAbove);
            menu.Items.Insert(insertIdx + 1, delete);
        }

        var pos = _dockConfigStore.Current.Position;
        foreach (var top in menu.Items.OfType<MenuItem>())
        {
            if (top.Tag is string topTag && topTag == "AutoHide")
            {
                top.IsChecked = _dockConfigStore.Current.AutoHide;
            }
            if (top.Tag is string asTag && asTag == "AutoStart")
            {
                top.IsChecked = _autoStart.IsEnabled;
            }
            if (top.Tag is string lockTag && lockTag == "Lock")
            {
                top.IsChecked = _dockConfigStore.Current.IsLocked;
            }
            if (top.Header is "Position")
            {
                foreach (var sub in top.Items.OfType<MenuItem>())
                {
                    if (sub.Tag is string edgeStr && Enum.TryParse<DockEdge>(edgeStr, out var edge))
                    {
                        sub.IsChecked = pos == edge;
                    }
                }
            }
        }
    }

    private void DockMenu_Closed(object sender, RoutedEventArgs e)
    {
        _isContextMenuOpen = false;
        _menuPollTimer?.Stop();
        _menuPollTimer = null;

        // If the user closed the menu by hovering away and the cursor is
        // outside the dock with auto-hide on, kick off the hide timer right
        // away so the dock slides out without waiting for another MouseLeave.
        if (_dockConfigStore.Current.AutoHide && !IsCursorNearDock())
        {
            StartHideTimer();
        }
    }

    // Cursor-position polling for close-on-leave. ContextMenu.MouseLeave
    // doesn't fire reliably because the popup is a separate top-level window,
    // and IsMouseOver lies for the same reason. So we poll the real cursor
    // position against the screen rect of every open menu / submenu.
    private System.Windows.Threading.DispatcherTimer? _menuPollTimer;
    private DateTime _menuOpenedAt;
    private const int MenuOpenGraceMs   = 400;  // ignore polling until user gets to the menu
    private const int MenuLeaveCloseMs  = 200;  // close once cursor is out for this long
    private DateTime _menuOutsideSince  = DateTime.MaxValue;

    private void StartMenuPolling(ContextMenu menu)
    {
        _menuPollTimer?.Stop();
        _menuOpenedAt    = DateTime.Now;
        _menuOutsideSince = DateTime.MaxValue;
        _menuPollTimer   = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(80),
        };
        _menuPollTimer.Tick += (_, _) =>
        {
            if (!menu.IsOpen) { _menuPollTimer?.Stop(); return; }
            // Grace period after opening so the cursor has time to reach the
            // first menu item even if it spawned a few pixels off the cursor.
            if ((DateTime.Now - _menuOpenedAt).TotalMilliseconds < MenuOpenGraceMs) { return; }

            if (IsCursorOverAnyOpenMenuPart(menu))
            {
                _menuOutsideSince = DateTime.MaxValue;
                return;
            }
            // Cursor is outside. Track for how long; only close after the
            // outside-streak passes MenuLeaveCloseMs so brief jumps between
            // main menu and a submenu's popup don't snap the menu shut.
            if (_menuOutsideSince == DateTime.MaxValue) { _menuOutsideSince = DateTime.Now; }
            if ((DateTime.Now - _menuOutsideSince).TotalMilliseconds >= MenuLeaveCloseMs)
            {
                menu.IsOpen = false;
            }
        };
        _menuPollTimer.Start();
    }

    private bool IsCursorOverAnyOpenMenuPart(ContextMenu menu)
    {
        if (!GetCursorPos(out var pt)) { return true; }   // err on the safe side
        var cursor = new Point(pt.X, pt.Y);
        // Main menu rect.
        if (TryGetScreenRect(menu, out var mainRect) && mainRect.Contains(cursor)) { return true; }
        // Each open submenu's child items live in a separate popup window;
        // check the child items' rects (their PointToScreen returns the
        // popup's actual screen coords).
        foreach (var fe in EnumerateOpenSubmenuItems(menu))
        {
            if (TryGetScreenRect(fe, out var rect))
            {
                rect.Inflate(2, 2);   // forgive 1-2 px borders between items
                if (rect.Contains(cursor)) { return true; }
            }
        }
        return false;
    }

    private static IEnumerable<FrameworkElement> EnumerateOpenSubmenuItems(ItemsControl parent)
    {
        foreach (var item in parent.Items)
        {
            if (item is MenuItem mi && mi.IsSubmenuOpen)
            {
                // Items shown in the submenu popup (Bottom / Top / Left / Right).
                foreach (var child in mi.Items)
                {
                    if (child is FrameworkElement fe) { yield return fe; }
                }
                // Recurse for deeper submenus.
                foreach (var nested in EnumerateOpenSubmenuItems(mi))
                {
                    yield return nested;
                }
            }
        }
    }

    private static bool TryGetScreenRect(FrameworkElement el, out Rect rect)
    {
        rect = default;
        if (!el.IsVisible || el.ActualWidth <= 0 || el.ActualHeight <= 0) { return false; }
        try
        {
            var tl = el.PointToScreen(new Point(0, 0));
            var br = el.PointToScreen(new Point(el.ActualWidth, el.ActualHeight));
            rect = new Rect(tl, br);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void PinFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Pin a file", Filter = "All files|*.*" };
        var initial = GetPickerInitialDirectory();
        if (initial is not null) { dialog.InitialDirectory = initial; }
        if (dialog.ShowDialog() != true) { return; }
        PinFiles([dialog.FileName], _pinnedRepo.Count);
    }

    private void PinFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Pin a folder" };
        var initial = GetPickerInitialDirectory();
        if (initial is not null) { dialog.InitialDirectory = initial; }
        if (dialog.ShowDialog() != true) { return; }
        PinFiles([dialog.FolderName], _pinnedRepo.Count);
    }

    // Initial directory for the Add file/folder pickers, anchored to where
    // the context menu was opened:
    //  - on a tile → that tile's folder (a folder pin anchors to ITSELF, a
    //    file/app pin to its containing directory),
    //  - on empty plate space → the folder of the tile NEAREST the click
    //    (measured along the dock's long axis),
    //  - separators / URL pins / broken paths are skipped and the search
    //    moves outward to the next-nearest tile,
    //  - no usable tile at all → null, and the dialog opens wherever the
    //    OS would have opened it anyway.
    private string? GetPickerInitialDirectory()
    {
        foreach (var vm in TilesByProximityToMenu())
        {
            var dir = DirectoryFromTile(vm);
            if (dir is not null)
            {
                LogEvent($"[menu] picker initial dir ← \"{vm.DisplayName}\": {dir}");
                return dir;
            }
        }
        return null;
    }

    // The clicked tile first (if any), then every other tile ordered by
    // distance from the menu-open point along the dock's long axis.
    private IEnumerable<PinnedItemViewModel> TilesByProximityToMenu()
    {
        var ctx = _menuContextTile;
        if (ctx is not null) { yield return ctx; }

        var horiz  = _itemsOrientation == Orientation.Horizontal;
        var scored = new List<(double Dist, PinnedItemViewModel Vm)>();
        for (var i = 0; i < PinnedItems.Count; i++)
        {
            var vm = PinnedItems[i];
            if (ReferenceEquals(vm, ctx)) { continue; }
            if (TilesHost.ItemContainerGenerator.ContainerFromIndex(i)
                is not FrameworkElement el) { continue; }
            try
            {
                var centre = el.TranslatePoint(
                    new Point(el.ActualWidth / 2, el.ActualHeight / 2), TilesHost);
                var dist = horiz
                    ? Math.Abs(centre.X - _menuOpenPoint.X)
                    : Math.Abs(centre.Y - _menuOpenPoint.Y);
                scored.Add((dist, vm));
            }
            catch { /* container not realized yet — skip */ }
        }
        foreach (var entry in scored.OrderBy(s => s.Dist))
        {
            yield return entry.Vm;
        }
    }

    private static string? DirectoryFromTile(PinnedItemViewModel vm)
    {
        if (vm.IsSeparator) { return null; }
        if (vm.Model.Kind == PinnedItemKind.Url) { return null; }
        var path = vm.TargetPath;
        if (string.IsNullOrWhiteSpace(path)) { return null; }
        try
        {
            if (Directory.Exists(path)) { return path; }   // folder pin → itself
            var parent = Path.GetDirectoryName(path);      // file/app pin → parent
            return !string.IsNullOrEmpty(parent) && Directory.Exists(parent)
                ? parent
                : null;
        }
        catch
        {
            return null;   // malformed path — treat as no anchor
        }
    }

    private void PinSeparator_Click(object sender, RoutedEventArgs e)
    {
        // Visual divider — has no target. Inserted JUST AFTER the tile the
        // user right-clicked on (so "Add separator" puts the divider next to
        // the current item). If invoked from the empty plate, append at the
        // end as a fallback.
        var sep = new PinnedItem
        {
            Kind        = PinnedItemKind.Separator,
            TargetPath  = string.Empty,
            DisplayName = "Separator",
        };
        int insertAt = _pinnedRepo.Count;
        var ctxTile  = _menuContextTile;
        if (ctxTile is not null)
        {
            var idx = PinnedItems.IndexOf(ctxTile);
            if (idx >= 0) { insertAt = idx + 1; }
            LogEvent($"[menu] PinSeparator — contextTile={ctxTile.DisplayName}, idx={idx}, insertAt={insertAt}");
        }
        else
        {
            LogEvent($"[menu] PinSeparator — contextTile=NULL, insertAt={insertAt} (end of dock)");
        }
        try   { _pinnedRepo.Add(sep, insertAt); }
        catch { /* swallow — repo cap or persistence error */ }
    }

    private void Position_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && Enum.TryParse<DockEdge>(mi.Tag?.ToString(), out var edge))
        {
            var oldEdge = _dockConfigStore.Current.Position;
            _dockConfigStore.UpdatePosition(edge);
            // followCursor=true so the dock jumps to whichever monitor the
            // user's cursor is on right now. That gives users an obvious way
            // to MOVE the dock between monitors: move cursor to target monitor,
            // open Position menu, pick the desired edge.
            PositionAtScreenEdge(edge, followCursor: true);
            LogEvent($"Position changed: {oldEdge} → {edge}");

            // Reset auto-hide for the new edge so the reveal zone moves with
            // the dock and the hidden offset is recomputed.
            if (_dockConfigStore.Current.AutoHide)
            {
                _isAutoHidden = false;          // force ApplyAutoHide to run
                _revealZoneHost.Hide();
                ApplyAutoHide(true, animate: false);
            }
        }
    }

    private void AutoStart_Click(object sender, RoutedEventArgs e)
    {
        var enable = !_autoStart.IsEnabled;
        if (enable) { _autoStart.Enable(); }
        else        { _autoStart.Disable(); }
        var path = Environment.ProcessPath ?? "(unknown)";
        LogEvent($"Auto-start: {(enable ? $"on → {path}" : "off")}");
    }

    private void Lock_Click(object sender, RoutedEventArgs e)
    {
        var newState = !_dockConfigStore.Current.IsLocked;
        _dockConfigStore.UpdateIsLocked(newState);
        LogEvent($"Lock dock: {(newState ? "on" : "off")}");
    }

    private void AutoHide_Click(object sender, RoutedEventArgs e)
    {
        var newState = !_dockConfigStore.Current.AutoHide;
        _dockConfigStore.UpdateAutoHide(newState);
        LogEvent($"Auto-hide: {(newState ? "on" : "off")}");
        if (newState)
        {
            // Schedule first hide after delay so the user can confirm the
            // dock didn't crash before it disappears.
            StartHideTimer();
        }
        else
        {
            _hideTimer?.Stop();
            ApplyAutoHide(false);
        }
    }

    // --- Auto-hide / reveal -------------------------------------------------

    private void OnDockMouseEnter(object sender, MouseEventArgs e)
    {
        _hideTimer?.Stop();
        if (_isAutoHidden) { ApplyAutoHide(false); }
    }

    private void OnDockMouseLeave(object sender, MouseEventArgs e)
    {
        if (!_dockConfigStore.Current.AutoHide) { return; }
        StartHideTimer();
    }

    // Mouse-wheel drives the overflow scroller along the dock's LONG axis
    // (a ScrollViewer only wheels its vertical axis natively, so Top/Bottom
    // docks need the wheel mapped onto the horizontal offset). Preview-level
    // so tile buttons can't swallow the event first. Handled ONLY when there
    // is actual overflow — a dock that fits leaves wheel events untouched.
    private void TilesScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_itemsOrientation == Orientation.Horizontal)
        {
            if (TilesScroller.ScrollableWidth <= 0) { return; }
            TilesScroller.ScrollToHorizontalOffset(TilesScroller.HorizontalOffset - e.Delta);
        }
        else
        {
            if (TilesScroller.ScrollableHeight <= 0) { return; }
            TilesScroller.ScrollToVerticalOffset(TilesScroller.VerticalOffset - e.Delta);
        }
        e.Handled = true;
    }

    private void OnRevealZonePointerEntered(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            // Cooldown guard: if the dock JUST hid, ignore an immediate
            // reveal trigger. This breaks the flicker loop that happens
            // when the cursor sits at the screen edge — the reveal zone
            // appears under it the moment hide finishes.
            if ((DateTime.Now - _lastAutoHideToggle).TotalMilliseconds < AutoHideCooldownMs)
            {
                return;
            }
            _hideTimer?.Stop();
            ApplyAutoHide(false);
            // Re-arm auto-hide right away. Normally the dock's own
            // MouseEnter/MouseLeave takes over once the cursor moves onto
            // it, but when this reveal was triggered by a DRAG hovering the
            // reveal zone (see RevealZoneWindow's drag events) no mouse
            // events fire at all — without this the dock would stay open
            // forever after an abandoned drag. The timer's GetCursorPos
            // poll keeps re-arming while the cursor stays near the dock,
            // so the mouse-hover path behaves as before.
            StartHideTimer();
        });
    }

    // External drag-to-pin while the dock is auto-hidden: reveal it so the
    // user can complete the drop without turning auto-hide off first.
    // Mouse events are suppressed during an OLE drag, so the normal
    // hover-to-reveal path can never fire — the DRAG events on the peek
    // pill (Window_DragOver / DockPlate_DragEnter) and on the reveal-zone
    // strip are the only signals available. Bypasses the reveal cooldown:
    // dragging a file onto the dock is an unambiguous "open up" intent.
    private void RevealForDrag()
    {
        if (!_isAutoHidden) { return; }
        if (_dockConfigStore.Current.IsLocked) { return; }
        LogEvent("[drag] reveal-for-drag: opening auto-hidden dock");
        _hideTimer?.Stop();
        ApplyAutoHide(false);
        // Re-arm: if the drag is abandoned (dragged away, never dropped),
        // the timer's cursor poll re-hides the dock; while the drag stays
        // near the dock each tick just re-arms instead.
        StartHideTimer();
    }

    private void StartHideTimer()
    {
        _hideTimer?.Stop();
        _hideTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(AutoHideDelayMs),
        };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer?.Stop();
            if (!_dockConfigStore.Current.AutoHide) { return; }
            if (_isDragInProgress)                  { return; }
            if (IsContextMenuOpen())                { return; }
            // Truth source for "is cursor near dock": real screen-pixel
            // GetCursorPos against the dock rect + a guard band. WPF's
            // IsMouseOver is unreliable mid-animation (window keeps moving,
            // events fire/un-fire each frame → flicker).
            if (IsCursorNearDock())                 { StartHideTimer(); return; }
            // Cooldown after just having shown: prevent rapid hide.
            if ((DateTime.Now - _lastAutoHideToggle).TotalMilliseconds < AutoHideCooldownMs)
            {
                StartHideTimer();
                return;
            }
            ApplyAutoHide(true);
        };
        _hideTimer.Start();
    }

    // Flag-based: each ContextMenu instance (Border + every tile Button) wires
    // the same Opened/Closed handlers and toggles _isContextMenuOpen, so this
    // covers them all without having to iterate tile container generators.
    private bool IsContextMenuOpen() => _isContextMenuOpen;

    // Real cursor position vs dock rect, extended all the way to the screen
    // edge that the dock anchors against. This is the key flicker fix:
    // when the user holds their cursor at the very screen edge to keep the
    // dock revealed, the cursor is BELOW the docked plate (there's a small
    // gap above the screen edge). Without the extension, MouseLeave fires
    // → hide → dock peek covers cursor → MouseEnter → show → loop.
    private bool IsCursorNearDock()
    {
        if (!GetCursorPos(out var pt)) { return false; }
        var cursor = new Point(pt.X, pt.Y);
        try
        {
            var topLeft     = PointToScreen(new Point(0, 0));
            var bottomRight = PointToScreen(new Point(ActualWidth, ActualHeight));
            var rect        = new Rect(topLeft, bottomRight);

            // Stretch rect to the screen edge on the docked side so the
            // hot zone includes the gap between dock and screen edge.
            var dpiTopLeft = PointToScreen(new Point(0, 0));
            // SystemParameters.PrimaryScreen* are in DIPs; convert via dpi.
            var dpi = GetSystemDpi();
            var screenW = SystemParameters.PrimaryScreenWidth  * dpi;
            var screenH = SystemParameters.PrimaryScreenHeight * dpi;
            switch (_dockConfigStore.Current.Position)
            {
                case DockEdge.Bottom: rect = new Rect(rect.Left, rect.Top,    rect.Width, screenH - rect.Top);    break;
                case DockEdge.Top:    rect = new Rect(rect.Left, 0,           rect.Width, rect.Bottom);            break;
                case DockEdge.Left:   rect = new Rect(0,         rect.Top,    rect.Right,  rect.Height);           break;
                case DockEdge.Right:  rect = new Rect(rect.Left, rect.Top,    screenW - rect.Left, rect.Height);   break;
            }
            rect.Inflate(8, 8);
            return rect.Contains(cursor);
        }
        catch
        {
            return false;                        // PointToScreen can throw if HWND not realized
        }
    }

    // Called by TrackingDragSource so auto-hide doesn't snap the dock away
    // while the user is mid-drag (cursor would leave the window during the
    // drag, which would normally trigger MouseLeave → hide timer).
    internal bool IsLocked => _dockConfigStore.Current.IsLocked;

    internal void MarkDragStarted() => _isDragInProgress = true;
    internal void MarkDragEnded()
    {
        _isDragInProgress = false;
        // If the cursor ended up outside the dock (drag-out case), the next
        // mouse-leave was suppressed — re-arm the hide timer here.
        if (_dockConfigStore.Current.AutoHide && !IsMouseOver) { StartHideTimer(); }
    }

    // Toggle hidden state. Hiding SLIDES the dock toward its anchored edge
    // AND collapses the perpendicular dimension to a 5-px peek. End state is
    // a thin strip flush with the edge: full-length along the dock axis,
    // peek-thick across it. The perpendicular shrink is what stops the dock
    // from leaking onto an adjacent monitor — without it, a Right-dock
    // hidden by sliding right would still have its body extending across the
    // seam into the next screen.
    private void ApplyAutoHide(bool hide, bool animate = true)
    {
        if (hide == _isAutoHidden) { return; }
        _lastAutoHideToggle = DateTime.Now;

        // Block OnDockSizeChanged across the whole transition (setup + anim).
        // AnimateAutoHide also sets this — duplicating here is fine: the
        // flag is idempotent and the animation's Completed handler is what
        // releases it.
        _isAutoHideAnimating = true;

        if (hide)
        {
            _shownWidth  = ActualWidth;
            _shownHeight = ActualHeight;
            var (sl, st) = ComputeShownPosition();
            _shownLeft = sl;
            _shownTop  = st;

            var (toLeft, toTop) = ComputeHiddenPosition();
            AnimateAutoHide(toLeft, toTop, animate, isShow: false);
            _revealZoneHost.Show(ComputeRevealRect());
            _isAutoHidden = true;
        }
        else
        {
            _revealZoneHost.Hide();
            _isAutoHidden = false;
            var (sl, st) = ComputeShownPosition();
            AnimateAutoHide(sl, st, animate, isShow: true);
        }
    }

    // Slide + perpendicular shrink. Animates Left, Top, Width, Height in
    // parallel using HandoffBehavior.SnapshotAndReplace so each new animation
    // picks up smoothly from whatever the current frame value is (no
    // BeginAnimation(null) + reset dance, which introduces a one-frame value
    // snap on multi-property animations).
    private void AnimateAutoHide(double targetLeft, double targetTop,
                                 bool animate, bool isShow)
    {
        // Set the guard BEFORE touching any animated property — even setting
        // Width directly can trigger SizeChanged on the next layout pass.
        _isAutoHideAnimating = true;

        if (!animate)
        {
            BeginAnimation(LeftProperty,    null);
            BeginAnimation(TopProperty,     null);
            BeginAnimation(OpacityProperty, null);
            PlateScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            PlateScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            DockContent.BeginAnimation(OpacityProperty, null);
            Left    = targetLeft;
            Top     = targetTop;
            Opacity = isShow ? 1.0 : AutoHidePeekOpacity;
            DockContent.Opacity = isShow ? 1.0 : 0.0;
            // Snap scale to the same ratio the animated path would settle at.
            var nahoriz = _itemsOrientation == Orientation.Horizontal;
            var naPerp  = nahoriz ? _shownHeight : _shownWidth;
            var naLong  = nahoriz ? _shownWidth  : _shownHeight;
            var naSnap  = naPerp > 0 ? AutoHidePeekPx / naPerp : 0.05;
            var naPill  = naLong > 0 ? Math.Min(1.0, AutoHidePillLongPx / naLong) : 1.0;
            PlateScale.ScaleX = isShow ? 1.0 : (nahoriz ? naPill : naSnap);
            PlateScale.ScaleY = isShow ? 1.0 : (nahoriz ? naSnap : naPill);
            _isAutoHideAnimating = false;
            return;
        }

        // SineEase EaseInOut per phase — gentlest of the built-in S-curves,
        // no perceptible mid-animation acceleration. Sine curves are also
        // mathematically symmetric so show plays exactly like hide in reverse.
        IEasingFunction ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        var dur = TimeSpan.FromMilliseconds(isShow ? AutoHideShowMs : AutoHideHideMs);

        // --- Two-phase choreography -------------------------------------
        // So SHOW reads as "slide up out of the screen edge" (the original
        // pre-pill effect) instead of "zoom out of the pill", the pill↔strip
        // transition and the emerge/retreat motion are SEQUENCED, not
        // overlapped:
        //   show: [0 → PillPhaseShow] pill widens ALONG the edge into the
        //         full-length peek strip (still flush with the edge, still
        //         AutoHidePeekPx thin), then
        //         [PillPhaseShow → 1] the strip expands + slides outward to
        //         the docked position — the classic emerge-from-edge motion.
        //   hide: exact mirror — slide in + collapse to the strip first
        //         [0 → PillPhaseHide], then the strip contracts to the pill
        //         [PillPhaseHide → 1].
        const double PillPhaseShow = 0.40;
        const double PillPhaseHide = 0.60;
        var slideStart = isShow ? PillPhaseShow : 0.0;
        var slideEnd   = isShow ? 1.0           : PillPhaseHide;
        var pillStart  = isShow ? 0.0           : PillPhaseHide;
        var pillEnd    = isShow ? PillPhaseShow : 1.0;

        // Phased key-frame track: hold `from` until startPct, ease to `to`
        // by endPct, hold `to` until the end. `from` is the property's
        // CURRENT (possibly mid-animation) value, so interrupting a hide
        // with a show (or vice versa) hands off from the exact on-screen
        // state with no snap — the keyframe equivalent of what a From-less
        // DoubleAnimation + SnapshotAndReplace did before phasing.
        DoubleAnimationUsingKeyFrames Phased(double from, double to, double startPct, double endPct)
        {
            var a = new DoubleAnimationUsingKeyFrames
            {
                Duration     = dur,
                FillBehavior = FillBehavior.HoldEnd,
            };
            a.KeyFrames.Add(new LinearDoubleKeyFrame(from, KeyTime.FromPercent(0.0)));
            if (startPct > 0.0)
            {
                a.KeyFrames.Add(new LinearDoubleKeyFrame(from, KeyTime.FromPercent(startPct)));
            }
            a.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromPercent(endPct)) { EasingFunction = ease });
            if (endPct < 1.0)
            {
                a.KeyFrames.Add(new LinearDoubleKeyFrame(to, KeyTime.FromPercent(1.0)));
            }
            return a;
        }

        // Scale the dock content alongside the window motion. CRITICAL: the
        // scale ratio must match the window's collapsing-axis ratio so the
        // tile visuals shrink at exactly the same rate as the window's
        // bounds — otherwise the content and the dock-plate edge get out of
        // sync mid-animation (icons "lag behind" or "outpace" the shrinking
        // dock outline).
        //   For Bottom/Top: window collapses Height → hiddenScale = peek / _shownHeight
        //   For Left/Right: window collapses Width  → hiddenScale = peek / _shownWidth
        var horiz = _itemsOrientation == Orientation.Horizontal;
        var perpShown = horiz ? _shownHeight : _shownWidth;
        var hiddenScale = perpShown > 0 ? AutoHidePeekPx / perpShown : 0.05;
        // LONG-axis pill scale: hidden state is a short pill (AutoHidePillLongPx)
        // instead of a full-length strip. RenderTransformOrigin's long-axis
        // component is 0.5 (see PositionAtScreenEdge), so the shrink collapses
        // toward the dock's centre and the pill stays centred + flush with the
        // screen edge. Min(1.0, …) guards docks already shorter than the pill.
        var longShown  = horiz ? _shownWidth : _shownHeight;
        var hiddenLong = longShown > 0 ? Math.Min(1.0, AutoHidePillLongPx / longShown) : 1.0;
        ScaleTransform scale = PlateScale;

        // Window position + perpendicular scale + window opacity all belong
        // to the SLIDE phase: they hold at the edge while the pill↔strip
        // transition plays, then move together. Left/Top only actually change
        // on the perpendicular axis (ComputeHiddenPosition keeps the long-
        // axis coordinate identical to the shown one), so keying both to the
        // slide window keeps the peek strip pinned to the screen edge for
        // the whole pill phase.
        var animLeft      = Phased(Left,    targetLeft, slideStart, slideEnd);
        var animTop       = Phased(Top,     targetTop,  slideStart, slideEnd);
        var animOpacity   = Phased(Opacity, isShow ? 1.0 : AutoHidePeekOpacity,
                                   slideStart, slideEnd);
        var animScaleAxis = Phased(horiz ? scale.ScaleY : scale.ScaleX,
                                   isShow ? 1.0 : hiddenScale,
                                   slideStart, slideEnd);
        // Long axis runs in the OTHER phase window: pill↔strip at the edge.
        var animScaleLong = Phased(horiz ? scale.ScaleX : scale.ScaleY,
                                   isShow ? 1.0 : hiddenLong,
                                   pillStart, pillEnd);
        // Inner items fade independently of the plate scaling so the peek
        // strip/pill is just clean background. Tied to the slide phase:
        //   Show: stay 0 while the pill widens (plate is a 5-px strip — any
        //         content would smear), fade 0 → 1 as the dock emerges,
        //         fully sharp before it lands (85%).
        //   Hide: hold 1 briefly, fade out while the plate retreats so it
        //         reaches the edge as clean background, BEFORE the strip
        //         contracts to the pill.
        var animTilesOpacity = isShow
            ? Phased(DockContent.Opacity, 1.0, PillPhaseShow, 0.85)
            : Phased(DockContent.Opacity, 0.0, 0.25, PillPhaseHide);
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(animLeft,         120);
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(animTop,          120);
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(animOpacity,      120);
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(animScaleAxis,    120);
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(animScaleLong,    120);
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(animTilesOpacity, 120);

        animScaleAxis.Completed += (_, _) => { _isAutoHideAnimating = false; };

        BeginAnimation(LeftProperty,    animLeft,    HandoffBehavior.SnapshotAndReplace);
        BeginAnimation(TopProperty,     animTop,     HandoffBehavior.SnapshotAndReplace);
        BeginAnimation(OpacityProperty, animOpacity, HandoffBehavior.SnapshotAndReplace);
        // Scale the perpendicular axis (the one that's collapsing); leave the
        // other axis at 1 so the strip keeps its long-axis length and the
        // edge anchor stays in place. Border + items shrink together as ONE
        // unit because the transform sits on the plate root — no Window
        // dimension animation needed (and no Win32/WPF-thread desync).
        scale.BeginAnimation(
            horiz ? ScaleTransform.ScaleYProperty : ScaleTransform.ScaleXProperty,
            animScaleAxis, HandoffBehavior.SnapshotAndReplace);
        // Long axis: full length ↔ short pill, sequenced into its own phase
        // window (see PillPhaseShow/PillPhaseHide above) so the pill↔strip
        // transition always plays flush against the screen edge.
        scale.BeginAnimation(
            horiz ? ScaleTransform.ScaleXProperty : ScaleTransform.ScaleYProperty,
            animScaleLong, HandoffBehavior.SnapshotAndReplace);
        // Fade inner tiles out independently so the hidden state is a clean
        // peek strip — no tiny squashed icons riding along on the collapsed
        // plate.
        DockContent.BeginAnimation(OpacityProperty, animTilesOpacity, HandoffBehavior.SnapshotAndReplace);
    }

    // Shown position: mirror of PositionAtScreenEdge's switch, computed
    // here as a pure function so ApplyAutoHide can ask "where should the
    // dock land when shown?" without physically moving it first. Uses the
    // same gap-zero-when-AutoHide rule as PositionAtScreenEdge.
    private (double Left, double Top) ComputeShownPosition()
    {
        var (work, fullBottom) = GetTargetScreenBounds();
        var w = work;
        const double gap = 20.0;
        return _dockConfigStore.Current.Position switch
        {
            DockEdge.Bottom => (
                w.Left + (w.Width - _shownWidth) / 2,
                fullBottom - _shownHeight - gap),
            DockEdge.Top    => (
                w.Left + (w.Width - _shownWidth) / 2,
                w.Top + gap),
            DockEdge.Left   => (
                w.Left + gap,
                w.Top + (w.Height - _shownHeight) / 2),
            DockEdge.Right  => (
                w.Right - _shownWidth - gap,
                w.Top + (w.Height - _shownHeight) / 2),
            _               => (Left, Top),
        };
    }

    // Hidden POSITION (Window keeps its full size; the dock-plate
    // ScaleTransform is what makes the visible content shrink to a peek
    // strip). Slides the Window so its screen-facing edge sits flush with
    // the monitor edge — the scaled-down Border then renders the peek strip
    // exactly at the screen boundary.
    private (double Left, double Top) ComputeHiddenPosition()
    {
        var (work, fullBottom) = GetTargetScreenBounds();
        var w = work;
        return _dockConfigStore.Current.Position switch
        {
            DockEdge.Bottom => (_shownLeft, fullBottom - _shownHeight),
            DockEdge.Top    => (_shownLeft, w.Top),
            DockEdge.Left   => (w.Left, _shownTop),
            DockEdge.Right  => (w.Right - _shownWidth, _shownTop),
            _               => (_shownLeft, _shownTop),
        };
    }

    // Reveal zone: 4-px-thick strip along the dock-anchored edge so the user
    // can bring the dock back without aiming precisely at the peek strip.
    // Bottom uses the real screen edge; other sides use the work-area edge of
    // the dock's CURRENT monitor (multi-monitor aware).
    private Windows.Graphics.RectInt32 ComputeRevealRect()
    {
        var dpi                = GetSystemDpi();
        var (work, fullBottom) = GetTargetScreenBounds();
        var w                  = work;
        const int thick = 4;
        int X(double dipX) => (int)Math.Round(dipX * dpi);
        int Y(double dipY) => (int)Math.Round(dipY * dpi);
        return _dockConfigStore.Current.Position switch
        {
            DockEdge.Bottom => new Windows.Graphics.RectInt32(
                X(_shownLeft), Y(fullBottom - thick), X(ActualWidth), thick),
            DockEdge.Top    => new Windows.Graphics.RectInt32(
                X(_shownLeft), Y(w.Top),              X(ActualWidth), thick),
            DockEdge.Left   => new Windows.Graphics.RectInt32(
                X(w.Left),     Y(_shownTop),          thick,          Y(ActualHeight)),
            DockEdge.Right  => new Windows.Graphics.RectInt32(
                X(w.Right - thick), Y(_shownTop),     thick,          Y(ActualHeight)),
            _               => default,
        };
    }

    private static double GetSystemDpi()
    {
        using var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
        return g.DpiX / 96.0;
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var asm = Assembly.GetExecutingAssembly();
        var version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? asm.GetName().Version?.ToString()
                      ?? "unknown";
        // Strip the +commit-sha suffix that SDK adds to InformationalVersion
        var plus = version.IndexOf('+');
        if (plus > 0) { version = version[..plus]; }

        MessageBox.Show(
            $"DockXI\nFloating Dock for Windows\n\nVersion {version}\n\n.NET 8 · WPF\nMIT licence",
            "About DockXI",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void Quit_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }


    // ------------------------------------------------------------------------
    // Override Win32 SM_CXMIN / SM_CYMIN (default ~132 × 38) so SizeToContent
    // can shrink the dock smaller than the OS-imposed minimum.
    // ------------------------------------------------------------------------

    private const int WM_GETMINMAXINFO = 0x0024;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT_W { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT_W ptReserved;
        public POINT_W ptMaxSize;
        public POINT_W ptMaxPosition;
        public POINT_W ptMinTrackSize;
        public POINT_W ptMaxTrackSize;
    }

    private IntPtr WndProc_MinMax(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            mmi.ptMinTrackSize = new POINT_W { X = 1, Y = 1 };
            Marshal.StructureToPtr(mmi, lParam, true);
        }
        return IntPtr.Zero;
    }


    // ------------------------------------------------------------------------
    // WM_DROPFILES — legacy Win32 shell drop. Used as fallback when OLE
    // drag-drop is blocked by UAC/UIPI cross-IL (Explorer = User IL drags to
    // DockXI = Admin IL). DragAcceptFiles(true) in OnSourceInitialized tells
    // Windows we accept this message; the shell then routes drops here when
    // OLE is unavailable.
    // ------------------------------------------------------------------------
    private const int WM_DROPFILES_MSG = 0x0233;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void DragAcceptFiles(IntPtr hWnd, bool fAccept);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr hDrop, uint iFile,
        System.Text.StringBuilder? lpszFile, uint cch);

    [DllImport("shell32.dll")]
    private static extern void DragFinish(IntPtr hDrop);

    private IntPtr WndProc_DropFiles(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_DROPFILES_MSG) { return IntPtr.Zero; }
        if (_dockConfigStore.Current.IsLocked)
        {
            DragFinish(wParam);
            handled = true;
            return IntPtr.Zero;
        }

        var hDrop = wParam;
        try
        {
            var count = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
            var paths = new string[count];
            for (uint i = 0; i < count; i++)
            {
                var len = DragQueryFile(hDrop, i, null, 0) + 1;
                var sb  = new System.Text.StringBuilder((int)len);
                DragQueryFile(hDrop, i, sb, len);
                paths[i] = sb.ToString();
            }
            if (paths.Length > 0)
            {
                PinFiles(paths, PinnedItems.Count);
            }
        }
        finally
        {
            DragFinish(hDrop);
        }
        handled = true;
        return IntPtr.Zero;
    }


    // --- Drag-out to unpin -----------------------------------------------

    internal void UnpinIfDraggedOutside(PinnedItemViewModel vm)
    {
        if (_dockConfigStore.Current.IsLocked) { return; }
        if (!GetCursorPos(out var pt)) { return; }
        var cursor      = new Point(pt.X, pt.Y);
        // Transform BOTH corners via PointToScreen so DPI scaling is applied to
        // the width/height too (high-DPI displays would otherwise produce a
        // rect smaller than the visible dock).
        var topLeft     = PointToScreen(new Point(0, 0));
        var bottomRight = PointToScreen(new Point(ActualWidth, ActualHeight));
        var rect        = new Rect(topLeft, bottomRight);
        if (rect.Contains(cursor)) { return; }   // dropped inside dock → keep
        try { _pinnedRepo.Remove(vm.Id); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DockXI] Unpin-on-drag-out failed: {ex.Message}");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    // --- Win32 styling ------------------------------------------------------

    private const int GWL_EXSTYLE                    = -20;
    private const int WS_EX_TOOLWINDOW              = 0x00000080;
    private static readonly IntPtr HWND_TOPMOST     = new(-1);
    private const uint SWP_NOMOVE                   = 0x0002;
    private const uint SWP_NOSIZE                   = 0x0001;
    private const uint SWP_NOACTIVATE               = 0x0010;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE  = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND                  = 2;
    private const int DWMWCP_DONOTROUND             = 1;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private static void ApplyToolWindowStyles(IntPtr hwnd)
    {
        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW));
    }

    private static void ApplyDarkTitleBar(IntPtr hwnd)
    {
        var on = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        var corner = DWMWCP_DONOTROUND;  // Border draws its own rounded corners — disable DWM rounding to avoid white seam
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
    }
}

// =============================================================================
// IDragSource — resets drop hints whenever drag ends.
// =============================================================================

internal sealed class TrackingDragSource : DefaultDragHandler
{
    private readonly MainDockWindow _owner;
    public TrackingDragSource(MainDockWindow owner) => _owner = owner;

    public override void StartDrag(IDragInfo dragInfo)
    {
        if (_owner.IsLocked) { dragInfo.Effects = DragDropEffects.None; return; }
        _owner.MarkDragStarted();
        base.StartDrag(dragInfo);
    }

    public override void DragCancelled()
    {
        base.DragCancelled();
        _owner.MarkDragEnded();
        _owner.ResetDropGap();
        _owner.HideInsertBar();
    }

    public override void DragDropOperationFinished(DragDropEffects op, IDragInfo info)
    {
        base.DragDropOperationFinished(op, info);
        _owner.MarkDragEnded();
        _owner.ResetDropGap();
        _owner.HideInsertBar();

        // Any time a dock tile drag finishes with the cursor outside the dock,
        // treat it as unpin — internal reorder always lands inside, so cursor
        // outside means the user intended to drop the item away.
        if (info.SourceItem is PinnedItemViewModel vm)
        {
            _owner.UnpinIfDraggedOutside(vm);
        }
    }
}

// =============================================================================
// IDropTarget — pipes insert index into push-aside + InsertBar; syncs repo.
// =============================================================================

internal sealed class GapDropHandler : DefaultDropHandler
{
    private readonly MainDockWindow _owner;
    private int _lastInsertIndex = int.MinValue;

    public GapDropHandler(MainDockWindow owner) => _owner = owner;

    public override void DragOver(IDropInfo dropInfo)
    {
        if (_owner.IsLocked)
        {
            dropInfo.Effects = DragDropEffects.None;
            dropInfo.DropTargetAdorner = null;
            return;
        }
        // External drag from Explorer: dropInfo.DragInfo is null. Data shape
        // depends on gong/Windows: sometimes IDataObject wrapper, sometimes
        // already a string[] of paths.
        var external = dropInfo.DragInfo is null && DataObjectHasFiles(dropInfo.Data);

        if (external)
        {
            dropInfo.Effects = DragDropEffects.Copy;
            dropInfo.DropTargetAdorner = DropTargetAdorners.Insert;
        }
        else
        {
            base.DragOver(dropInfo);
        }

        var idx = dropInfo.InsertIndex;
        if (idx != _lastInsertIndex)
        {
            _lastInsertIndex = idx;
            _owner.ShowDropGap(idx, dropInfo.Data);
            _owner.ShowInsertBar(idx);
        }
    }

    private static bool DataObjectHasFiles(object? data) =>
        data is IDataObject d && d.GetDataPresent(DataFormats.FileDrop) ||
        data is string[];

    private static string[]? ExtractPaths(object? data)
    {
        if (data is string[] arr) { return arr; }
        if (data is IDataObject d && d.GetDataPresent(DataFormats.FileDrop)
            && d.GetData(DataFormats.FileDrop) is string[] paths) { return paths; }
        return null;
    }

    public override void Drop(IDropInfo dropInfo)
    {
        if (_owner.IsLocked) { return; }
        if (dropInfo.DragInfo is null && ExtractPaths(dropInfo.Data) is { } paths)
        {
            _owner.PinFiles(paths, dropInfo.InsertIndex);
        }
        else
        {
            base.Drop(dropInfo);
            _owner.SyncReorderToRepository();
        }
        _lastInsertIndex = int.MinValue;
        _owner.ResetDropGap();
        _owner.HideInsertBar();
    }
}

// =============================================================================
// BoolToOpacityConverter — true → 0.55 (broken/disabled), false → 1.0 (normal).
// =============================================================================

public sealed class BoolToOpacityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => (value is bool b && b) ? 0.55 : 1.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}
