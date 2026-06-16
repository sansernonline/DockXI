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

namespace DockXI.WpfShell;

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
        var dir = System.IO.Path.GetDirectoryName(typeof(MainDockWindow).Assembly.Location);
        while (!string.IsNullOrEmpty(dir))
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(dir, "DockXI.sln")))
            {
                return System.IO.Path.Combine(dir, "logs", "activity.log");
            }
            dir = System.IO.Path.GetDirectoryName(dir);
        }
        // Fallback: next to the exe so we never write to a random spot.
        return System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(typeof(MainDockWindow).Assembly.Location)!,
            "activity.log");
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
            System.IO.File.AppendAllText(ActivityLogFile,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}\n");
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
    private const int    AutoHideShowMs        = 400;  // slide IN duration (gentle reveal)
    private const int    AutoHideHideMs        = 260; // slide OUT duration (snappy hide)
    private const double AutoHidePeekPx        =   5.0; // visible strip when hidden
    private const int    AutoHideDelayMs       = 400; // delay after mouse leave
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
        }
    }

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
    private const double BouncePad = 5.0;
    private const double BasePad   = 5.0;
    public Thickness PlatePadding => _itemsOrientation == Orientation.Horizontal
        ? new Thickness(BasePad, BouncePad, BasePad, BasePad)     // Bottom/Top: extra top so bounce-up has room
        : new Thickness(BasePad);                                 // Left/Right: symmetric H-padding → icons horizontally centered

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
        PinnedItems.Add(vm);
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

    private void PositionAtScreenEdge(DockEdge edge)
    {
        // Flip the items panel BEFORE measuring so SizeToContent picks the new
        // orientation. Top/Bottom → row, Left/Right → column.
        ItemsOrientation = edge is DockEdge.Left or DockEdge.Right
            ? Orientation.Vertical
            : Orientation.Horizontal;
        TileFlowDirection = edge == DockEdge.Right
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;

        // Clear any in-flight auto-hide animation so a direct Left/Top
        // assignment below isn't overridden by an animation hold-end.
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty,  null);

        // WorkArea excludes the taskbar. Use it for Top/Left/Right docks
        // so the dock sits above whatever system UI is on those edges.
        // For Bottom dock specifically, anchor to the actual screen bottom
        // so the dock can sit flush with the monitor edge.
        var w           = SystemParameters.WorkArea;
        var screenBottom = SystemParameters.PrimaryScreenHeight;
        UpdateLayout();
        const double EdgeGapPx = 15.0;           // gap between dock and screen edge
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

    private void Window_DragOver(object sender, DragEventArgs e)
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
        }
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (_dockConfigStore.Current.IsLocked) { return; }
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) { return; }
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) { return; }
        if (paths.Length == 0) { return; }
        PinFiles(paths, PinnedItems.Count);
        e.Handled = true;
    }

    // --- External file drop (Border-level fallback for empty dock + padding) ---

    private void DockPlate_DragEnter(object sender, DragEventArgs e)
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
        if (_dockConfigStore.Current.IsLocked) { return; }
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) { return; }
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) { return; }
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

    // --- Context menu -------------------------------------------------------

    // True while ANY ContextMenu instance from this shared resource is open.
    // Auto-hide + topmost timers respect this so they don't close the menu
    // out from under the user mid-selection.
    private bool _isContextMenuOpen;

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

        // If opened from a tile (Button with PinnedItemViewModel context),
        // inject a "Delete \"<name>\"" entry just before the separator that
        // precedes "About DockXI" — so the item-specific action sits with
        // the dock-settings group, visually separated from About / Quit.
        if (menu.PlacementTarget is Button btn && btn.DataContext is PinnedItemViewModel vm)
        {
            var delete = new MenuItem
            {
                Header      = $"Delete \"{vm.DisplayName}\"",
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
        // Any open submenu popup (Position ▸ etc.).
        foreach (var sub in EnumerateOpenSubmenus(menu))
        {
            if (TryGetScreenRect(sub, out var subRect) && subRect.Contains(cursor)) { return true; }
        }
        return false;
    }

    private static IEnumerable<MenuItem> EnumerateOpenSubmenus(ItemsControl parent)
    {
        foreach (var item in parent.Items)
        {
            if (item is MenuItem mi && mi.IsSubmenuOpen)
            {
                yield return mi;
                foreach (var nested in EnumerateOpenSubmenus(mi))
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
        if (dialog.ShowDialog() != true) { return; }
        PinFiles([dialog.FileName], _pinnedRepo.Count);
    }

    private void PinFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Pin a folder" };
        if (dialog.ShowDialog() != true) { return; }
        PinFiles([dialog.FolderName], _pinnedRepo.Count);
    }

    private void Position_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && Enum.TryParse<DockEdge>(mi.Tag?.ToString(), out var edge))
        {
            var oldEdge = _dockConfigStore.Current.Position;
            _dockConfigStore.UpdatePosition(edge);
            PositionAtScreenEdge(edge);
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
        LogEvent($"Auto-start: {(enable ? "on" : "off")}");
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
        });
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

    // Toggle hidden state. When hiding, animate to a position where only a
    // 2-px peek strip remains on-screen and show the reveal-zone sentinel.
    // When showing, hide the sentinel and animate back to the docked spot.
    private void ApplyAutoHide(bool hide, bool animate = true)
    {
        if (hide == _isAutoHidden) { return; }
        _lastAutoHideToggle = DateTime.Now;

        if (hide)
        {
            // Remember docked position so we can slide back to it later.
            _shownLeft = Left;
            _shownTop  = Top;
            var (toLeft, toTop) = ComputeHiddenPosition();
            AnimateWindowTo(toLeft, toTop, animate, isShow: false);
            _revealZoneHost.Show(ComputeRevealRect());
            _isAutoHidden = true;
        }
        else
        {
            _revealZoneHost.Hide();
            AnimateWindowTo(_shownLeft, _shownTop, animate, isShow: true);
            _isAutoHidden = false;
        }
    }

    // Slide to (left, top). Show uses a longer duration + SineEase EaseOut
    // for a buttery reveal; hide is shorter + QuinticEase EaseIn so the dock
    // gets out of the way quickly. 120fps frame hint reduces jitter when the
    // OS moves the HWND each tick.
    private void AnimateWindowTo(double targetLeft, double targetTop, bool animate, bool isShow)
    {
        // CRITICAL: read the current animated value BEFORE clearing the
        // animation. Without this, the value snaps back to the property's
        // local value (which may be stale from a non-animated set earlier)
        // and the next animation appears to start from the wrong place —
        // making the first show after app-startup look much faster than
        // subsequent shows.
        var currentLeft = Left;
        var currentTop  = Top;
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty,  null);
        Left = currentLeft;
        Top  = currentTop;

        if (!animate)
        {
            Left = targetLeft;
            Top  = targetTop;
            return;
        }

        IEasingFunction ease = isShow
            ? new SineEase    { EasingMode = EasingMode.EaseOut }
            : new QuinticEase { EasingMode = EasingMode.EaseIn };
        var dur = TimeSpan.FromMilliseconds(isShow ? AutoHideShowMs : AutoHideHideMs);
        var animLeft = new DoubleAnimation
        {
            From           = currentLeft,
            To             = targetLeft,
            Duration       = dur,
            EasingFunction = ease,
            FillBehavior   = FillBehavior.HoldEnd,
        };
        var animTop = new DoubleAnimation
        {
            From           = currentTop,
            To             = targetTop,
            Duration       = dur,
            EasingFunction = ease,
            FillBehavior   = FillBehavior.HoldEnd,
        };
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(animLeft, 120);
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(animTop,  120);
        BeginAnimation(LeftProperty, animLeft);
        BeginAnimation(TopProperty,  animTop);
    }

    // Hidden position: slide perpendicular to the dock edge so only the peek
    // strip remains visible. Bottom uses the screen edge; other edges use
    // the WorkArea so the dock peeks above any system UI on that side.
    private (double Left, double Top) ComputeHiddenPosition()
    {
        var w           = SystemParameters.WorkArea;
        var screenBottom = SystemParameters.PrimaryScreenHeight;
        return _dockConfigStore.Current.Position switch
        {
            DockEdge.Bottom => (_shownLeft, screenBottom - AutoHidePeekPx),
            DockEdge.Top    => (_shownLeft, w.Top - ActualHeight + AutoHidePeekPx),
            DockEdge.Left   => (w.Left - ActualWidth + AutoHidePeekPx, _shownTop),
            DockEdge.Right  => (w.Right - AutoHidePeekPx, _shownTop),
            _               => (_shownLeft, _shownTop),
        };
    }

    // Reveal zone: 4-px-thick strip along the dock-anchored edge so the user
    // can bring the dock back without aiming precisely at the peek strip.
    // Bottom uses the real screen edge; other sides use the WorkArea edge.
    private Windows.Graphics.RectInt32 ComputeRevealRect()
    {
        var dpi          = GetSystemDpi();
        var w            = SystemParameters.WorkArea;
        var screenBottom = SystemParameters.PrimaryScreenHeight;
        const int thick = 4;
        int X(double dipX) => (int)Math.Round(dipX * dpi);
        int Y(double dipY) => (int)Math.Round(dipY * dpi);
        return _dockConfigStore.Current.Position switch
        {
            DockEdge.Bottom => new Windows.Graphics.RectInt32(
                X(_shownLeft), Y(screenBottom - thick), X(ActualWidth), thick),
            DockEdge.Top    => new Windows.Graphics.RectInt32(
                X(_shownLeft), Y(w.Top),            X(ActualWidth), thick),
            DockEdge.Left   => new Windows.Graphics.RectInt32(
                X(w.Left),     Y(_shownTop),        thick,          Y(ActualHeight)),
            DockEdge.Right  => new Windows.Graphics.RectInt32(
                X(w.Right - thick), Y(_shownTop),   thick,          Y(ActualHeight)),
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
