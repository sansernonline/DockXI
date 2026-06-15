using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DockXI.Contracts;
using GongSolutions.Wpf.DragDrop;
using Microsoft.Win32;

namespace DockXI.WpfShell;

public partial class MainDockWindow : Window, INotifyPropertyChanged
{
    private const double DropGapPx = 28.0;
    private const int    GapAnimMs = 180;

    private readonly IPinnedItemRepository _pinnedRepo;
    private readonly ILaunchService        _launchService;
    private readonly IIconExtractor        _iconExtractor;
    private readonly IShortcutResolver     _shortcutResolver;
    private readonly IDockConfigStore      _dockConfigStore;


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
        IDockConfigStore      dockConfigStore)
    {
        _pinnedRepo       = pinnedRepo;
        _launchService    = launchService;
        _iconExtractor    = iconExtractor;
        _shortcutResolver = shortcutResolver;
        _dockConfigStore  = dockConfigStore;

        InitializeComponent();
        DragHandler = new TrackingDragSource(this);
        DropHandler = new GapDropHandler(this);

        foreach (var item in _pinnedRepo.Items)
        {
            CreateViewModel(item);
        }

        _pinnedRepo.ItemAdded   += OnItemAdded;
        _pinnedRepo.ItemRemoved += OnItemRemoved;
        _launchService.ProcessSnapshotUpdated += OnProcessSnapshotUpdated;

    }

    // --- Startup / teardown -------------------------------------------------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc_MinMax);

        // The HWND was already sized to the Win32 minimum (~132×38) before
        // the hook attached. Toggle SizeToContent off→on to force WPF to
        // re-measure with the new (1×1) MinTrackSize constraint, then we
        // shrink to the actual content size.
        var current = SizeToContent;
        SizeToContent = SizeToContent.Manual;
        SizeToContent = current;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
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
    }

    protected override void OnClosed(EventArgs e)
    {
        _pinnedRepo.ItemAdded   -= OnItemAdded;
        _pinnedRepo.ItemRemoved -= OnItemRemoved;
        _launchService.ProcessSnapshotUpdated -= OnProcessSnapshotUpdated;
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
        Dispatcher.Invoke(() =>
        {
            var vm = CreateViewModel(e.Item);
            _ = vm.LoadIconAsync(_iconExtractor, GetDpi());
        });
    }

    private void OnItemRemoved(object? sender, PinnedItemEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            var vm = PinnedItems.FirstOrDefault(v => v.Id == e.Item.Id);
            if (vm is not null) { PinnedItems.Remove(vm); }
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

        var w = SystemParameters.WorkArea;
        UpdateLayout();
        switch (edge)
        {
            case DockEdge.Bottom:
                Left = w.Left + (w.Width - ActualWidth) / 2;
                Top  = w.Bottom - ActualHeight - 24;
                break;
            case DockEdge.Top:
                Left = w.Left + (w.Width - ActualWidth) / 2;
                Top  = w.Top + 24;
                break;
            case DockEdge.Left:
                Left = w.Left + 24;
                Top  = w.Top + (w.Height - ActualHeight) / 2;
                break;
            case DockEdge.Right:
                Left = w.Right - ActualWidth - 24;
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

            AnimateX(tt, to);
        }
    }

    internal void ResetDropGap()
    {
        for (var i = 0; i < TilesHost.Items.Count; i++)
        {
            var tt = GetTileTranslate(i);
            if (tt is null) { continue; }
            AnimateX(tt, 0.0);
        }
    }

    // --- Insert bar ---------------------------------------------------------

    internal void ShowInsertBar(int targetIndex)
    {
        var x = ComputeInsertX(targetIndex);
        if (x is null) { return; }
        InsertBarT.X = x.Value;
        InsertBar.Visibility = Visibility.Visible;
    }

    internal void HideInsertBar() => InsertBar.Visibility = Visibility.Collapsed;

    private double? ComputeInsertX(int targetIndex)
    {
        var count = TilesHost.Items.Count;
        if (count == 0)
        {
            // Empty dock: bar in the middle of the placeholder area.
            return TilesHost.ActualWidth / 2.0;
        }

        if (targetIndex <= 0)
        {
            var first = TilesHost.ItemContainerGenerator.ContainerFromIndex(0) as FrameworkElement;
            if (first is null) { return null; }
            return first.TransformToVisual(TilesHost).Transform(new Point(0, 0)).X;
        }
        if (targetIndex >= count)
        {
            var last = TilesHost.ItemContainerGenerator.ContainerFromIndex(count - 1) as FrameworkElement;
            if (last is null) { return null; }
            return last.TransformToVisual(TilesHost).Transform(new Point(last.ActualWidth, 0)).X;
        }
        var prev = TilesHost.ItemContainerGenerator.ContainerFromIndex(targetIndex - 1) as FrameworkElement;
        var curr = TilesHost.ItemContainerGenerator.ContainerFromIndex(targetIndex)     as FrameworkElement;
        if (prev is null || curr is null) { return null; }
        var r = prev.TransformToVisual(TilesHost).Transform(new Point(prev.ActualWidth, 0)).X;
        var l = curr.TransformToVisual(TilesHost).Transform(new Point(0, 0)).X;
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
        return slot?.RenderTransform as TranslateTransform;
    }

    private static void AnimateX(TranslateTransform tt, double to)
    {
        var anim = new DoubleAnimation
        {
            To             = to,
            Duration       = TimeSpan.FromMilliseconds(GapAnimMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior   = FillBehavior.HoldEnd,
        };
        tt.BeginAnimation(TranslateTransform.XProperty, anim);
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

    // --- External file drop (Border-level fallback for empty dock + padding) ---

    private void DockPlate_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void DockPlate_DragOver(object sender, DragEventArgs e)
    {
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

    private void DockMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) { return; }
        var pos = _dockConfigStore.Current.Position;
        foreach (var top in menu.Items.OfType<MenuItem>())
        {
            if (top.Tag is string topTag && topTag == "AutoHide")
            {
                top.IsChecked = _dockConfigStore.Current.AutoHide;
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
            _dockConfigStore.UpdatePosition(edge);
            PositionAtScreenEdge(edge);
        }
    }

    private void AutoHide_Click(object sender, RoutedEventArgs e)
    {
        _dockConfigStore.UpdateAutoHide(!_dockConfigStore.Current.AutoHide);
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

    // --- Win32 styling ------------------------------------------------------

    private const int GWL_EXSTYLE                    = -20;
    private const int WS_EX_TOOLWINDOW              = 0x00000080;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE  = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND                  = 2;

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
        var corner = DWMWCP_ROUND;
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

    public override void DragCancelled()
    {
        base.DragCancelled();
        _owner.ResetDropGap();
        _owner.HideInsertBar();
    }

    public override void DragDropOperationFinished(DragDropEffects op, IDragInfo info)
    {
        base.DragDropOperationFinished(op, info);
        _owner.ResetDropGap();
        _owner.HideInsertBar();
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
        var external = dropInfo.DragInfo is null
            && dropInfo.Data is IDataObject d
            && d.GetDataPresent(DataFormats.FileDrop);

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

    public override void Drop(IDropInfo dropInfo)
    {
        if (dropInfo.DragInfo is null
            && dropInfo.Data is IDataObject d
            && d.GetDataPresent(DataFormats.FileDrop)
            && d.GetData(DataFormats.FileDrop) is string[] paths)
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
