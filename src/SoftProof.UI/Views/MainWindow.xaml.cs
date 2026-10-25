using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SoftProof.Core.Analysis;
using SoftProof.Core.Models;
using SoftProof.Core.Services;
using SoftProof.RIPParsers.Raster;
using SoftProof.RIPParsers.Raster;
using SoftProof.RIPParsers.Raster.Tiff;
using SoftProof.UI.Services;
using SoftProof.UI.ViewModels;
using SoftProof.Viewer;

namespace SoftProof.UI.Views;

/// <summary>
/// Main window. Wires the plate viewer and rulers to the view model; all logic lives in the view model.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly FileWatchService _watch = new();
    private IReadOnlyList<PlateLayer> _plates = Array.Empty<PlateLayer>();
    private int _plateWidth;
    private int _plateHeight;
    private bool _syncingCompare;
    private bool _compareLayoutActive;
    private RipComparison? _lastComparison;
    private CancellationTokenSource? _detailCts;
    private Rect _detailRequestedRegion;
    private bool _detailPending;
    private int _detailGeneration;
    private (double CenterX, double CenterY, double Zoom)? _pendingView;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        
        // View model -> viewer
        _viewModel.PlatesLoaded += OnPlatesLoaded;
        _viewModel.PlatesChanged += (_, _) =>
        {
            _detailTiles.Clear();
            Viewer.Refresh();
        };
        _viewModel.ZoomRequested += (_, zoom) =>
        {
            Viewer.ZoomNativeTo(zoom);

            // Side-by-side: the toolbar zoom must move both panes, keeping their centres aligned.
            if (_viewModel.IsCompareMode)
            {
                var state = Viewer.GetViewState();
                CompareViewer.SetViewCenter(state.CenterX, state.CenterY, state.Zoom);
            }
        };
        _viewModel.FitToWindowRequested += (_, _) => Viewer.FitToView();
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        
        // Viewer -> view model
        Viewer.ViewChanged += (_, _) =>
        {
            OnViewChanged();

            // Zoom/pan on the left pane must drive the compare pane as well, otherwise
            // side-by-side looks frozen on the right.
            SyncCompare(Viewer, CompareViewer);
        };
        Viewer.CursorMoved += (_, point) => _viewModel.OnCursorMoved(point);
        Viewer.RectangleChanged += (_, e) => _viewModel.OnRectangleMeasured(e.Bounds, e.Completed);
        CompareViewer.CursorMoved += (_, point) => _viewModel.OnCursorMoved(point);
        Viewer.CaliperChanged += (_, args) => _viewModel.OnCaliperChanged(args.Start, args.End);

        // Watched RIP folders: auto-open a newly finished file in the background.
        _folderWatch.FileDetected += OnFolderFileDetected;
        _viewModel.WatchFoldersChanged += (_, folders) => _folderWatch.Watch(folders);
        _folderWatch.Watch(_viewModel.WatchFolders);

        // Navigator: drag the viewport to pan; the viewer keeps its own zoom.
        Navigator.CenterRequested += (_, centre) => Viewer.SetViewCenter(centre.X, centre.Y);
        Viewer.DetailRequested += OnDetailRequested;
        CompareViewer.DetailRequested += OnDetailRequested;

        // Auto-refresh when the RIP rewrites the open file.
        _watch.FileChanged += OnFileRewrittenByRip;

        Viewer.Tool = _viewModel.ActiveTool;
        Viewer.ViewMode = _viewModel.ViewMode;
        Viewer.ShowContentOverlay = _viewModel.ShowContentOverlay;
        Viewer.SimulateMetallicPaper = _viewModel.SimulateMetallicPaper;
        UpdatePaperColor();
        Viewer.BackSideOpacity = _viewModel.BackSideOpacity;
        _viewModel.ViewCommandRequested += OnViewCommand;
        _viewModel.CompareModeChanged += (_, _) => RefreshCompare();
        CompareOverlay.DifferenceThreshold = _viewModel.DifferenceThreshold;
        CompareOverlay.DifferenceMeasured += (_, percent) =>
        {
            _viewModel.DifferenceSummary = $"{percent:0.00}%";
            _viewModel.StatusText = $"Difference: {percent:0.00}% of the compared area";
        };
        CompareViewer.ViewChanged += (_, _) => SyncCompare(CompareViewer, Viewer);

        _viewModel.JobClosed += (_, _) =>
        {
            Viewer.Clear();
            Navigator.Clear();
            Viewer.SetContentBounds(Rect.Empty);
        };

        var queueView = System.Windows.Data.CollectionViewSource.GetDefaultView(_viewModel.RipQueue);
        if (queueView.CanGroup)
        {
            queueView.GroupDescriptions.Clear();
            queueView.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription("JobName"));
        }

        _viewModel.PreferencesRequested += (_, settings) =>
        {
            var window = new PreferencesWindow(settings.Preflight ?? new PreflightSettings()) { Owner = this };
            if (window.ShowDialog() != true)
                return;

            _viewModel.SaveCurrentPressProfile();
            if (_viewModel.CurrentJob is not null)
                _viewModel.RunPreflightCommand.Execute(null);
        };
        Viewer.ApertureSize = _viewModel.Aperture;
        Viewer.RectangleMeasure = _viewModel.RectangleMeasure;
        LeftPanelColumn.Width = _viewModel.ShowPreflightPane ? new GridLength(300) : new GridLength(0);
        LeftPanelBorder.Visibility = _viewModel.ShowPreflightPane ? Visibility.Visible : Visibility.Collapsed;
        Viewer.PreviewMouseMove += Viewer_PreviewMouseMove;
        UpdateRulers();
    }
    
    // ------------------------------------------------------------------------------------------
    // View model -> viewer
    // ------------------------------------------------------------------------------------------
    
    private void OnPlatesLoaded(object? sender, PlateSetEventArgs e)
    {
        Viewer.ViewMode = _viewModel.ViewMode;
        Viewer.ProgressiveCount = _viewModel.ProgressiveCount;
        Viewer.ApertureSize = _viewModel.Aperture;
        
        if (e.Plates.Count == 0)
        {
            Viewer.Clear();
            Viewer.SetContentBounds(Rect.Empty);
            Navigator.Clear();
        }
        else
        {
            _plates = e.Plates;
            _plateWidth = e.Width;
            _plateHeight = e.Height;
            DisposeSources();
            Viewer.PixelsPerMm = e.PixelsPerMm;
            Viewer.SetDetail(null, Rect.Empty);
            Viewer.SetPlates(e.Plates, e.Width, e.Height);
            Viewer.SetContentBounds(UnionInkBounds(e.Plates));
            Navigator.SetPlates(e.Plates, e.Width, e.Height);

            // Restore zoom/pan after a RIP rewrite, then watch the file for the next one.
            if (_pendingView is { } state)
            {
                _pendingView = null;
                Viewer.SetViewCenter(state.CenterX, state.CenterY, state.Zoom);
            }
        }

        _detailTiles.Clear();
        UpdateNavigator();
        WatchCurrentJob();
        UpdateRulers();
    }

    private void WatchCurrentJob()
    {
        var path = _viewModel.CurrentJob?.FilePath;
        if (_viewModel.AutoRefresh && !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            _watch.Watch(path);
        else
            _watch.Stop();
    }

    private void OnFileRewrittenByRip(object? sender, string path)
    {
        // FileSystemWatcher fires on a worker thread: marshal before touching the UI.
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (!_viewModel.AutoRefresh)
                return;

            _pendingView = Viewer.GetViewState();
            await _viewModel.LoadJobAsync(path);
        }));
    }

    /// <summary>Union of the ink bounds of every visible plate, in base-level pixels.</summary>
    private static Rect UnionInkBounds(IReadOnlyList<PlateLayer> plates)
    {
        var minX = int.MaxValue;
        var minY = int.MaxValue;
        var maxX = -1;
        var maxY = -1;

        foreach (var plate in plates)
        {
            if (plate.Separation.IsTechnical || plate.InkBounds.IsEmpty)
                continue;

            var bounds = plate.InkBounds;
            if (bounds.X < minX) minX = bounds.X;
            if (bounds.Y < minY) minY = bounds.Y;
            if (bounds.X + bounds.Width > maxX) maxX = bounds.X + bounds.Width;
            if (bounds.Y + bounds.Height > maxY) maxY = bounds.Y + bounds.Height;
        }

        return maxX < 0 ? Rect.Empty : new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    private void OnViewCommand(object? sender, string command)
    {
        if (command.StartsWith("rotate:", StringComparison.Ordinal) &&
            int.TryParse(command.AsSpan("rotate:".Length), out var degrees))
        {
            Viewer.SetRotation(degrees);
            CompareViewer.SetRotation(degrees);
        }
        else
        {
            switch (command)
            {
                case "rotate":
                    Viewer.RotateClockwise();
                    CompareViewer.RotateClockwise();
                    break;
                case "fliph":
                    Viewer.ToggleFlipHorizontal();
                    CompareViewer.ToggleFlipHorizontal();
                    break;
                case "flipv":
                    Viewer.ToggleFlipVertical();
                    CompareViewer.ToggleFlipVertical();
                    break;
                case "reset":
                    Viewer.ResetOrientation();
                    CompareViewer.ResetOrientation();
                    break;
            }
        }

        _viewModel.OrientationText = Viewer.OrientationLabel;
        _viewModel.RotationDegrees = Viewer.Orientation.Rotation;
        _viewModel.IsFlipHorizontal = Viewer.Orientation.FlipHorizontal;
        _viewModel.IsFlipVertical = Viewer.Orientation.FlipVertical;
        _viewModel.ViewTransformText = Viewer.Orientation.IsIdentity
            ? "View transform: none"
            : "View transform: " + Viewer.Orientation.Label();
    }

    /// <summary>Click-to-focus: a warning with a region zooms the viewer straight to it.</summary>
    private void PreflightList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is PreflightItem { Focus: { } focus })
            Viewer.FocusOn(new Rect(focus.X, focus.Y, focus.Width, focus.Height));
    }

    /// <summary>
    /// Rebuild both panes when compare mode or a pane selection changes. The left pane keeps the
    /// current zoom/pan; the right pane is then aligned to it so the views stay locked.
    /// </summary>
    private void RefreshCompare()
    {
        var on = _viewModel.IsCompareMode;
        CompareBar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        var sideBySide = on && _viewModel.ComparePresentation == PlateComparePresentation.SideBySide;

        // Both panes share one row, so they always have the same height and alignment.
        LeftPaneHeader.Visibility = sideBySide ? Visibility.Visible : Visibility.Collapsed;
        RightPane.Visibility = sideBySide ? Visibility.Visible : Visibility.Collapsed;
        CompareSplitter.Visibility = sideBySide ? Visibility.Visible : Visibility.Collapsed;

        // Only reset the split when the layout actually changes, so a user-dragged splitter
        // position survives plate switches.
        if (sideBySide != _compareLayoutActive)
        {
            SplitterColumn.Width = sideBySide ? new GridLength(6) : new GridLength(0);
            RightColumn.Width = sideBySide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            _compareLayoutActive = sideBySide;
        }

        CompareOverlay.Visibility = on && !sideBySide ? Visibility.Visible : Visibility.Collapsed;

        if (sideBySide)
        {
            // The right pane is a full viewer: same tool (select-to-zoom, measure, densitometer),
            // same view mode and the same zoom as the left pane.
            CompareViewer.Tool = _viewModel.ActiveTool;
            CompareViewer.ViewMode = _viewModel.ViewMode;
            // Opening compare must not land on the plate's white margin: centre both panes
            // on the artwork, then share that state.
            if (_viewModel.HasRaster)
                Viewer.CentreOnPlate();

            var paneState = Viewer.GetViewState();
            CompareViewer.SetViewCenter(paneState.CenterX, paneState.CenterY, paneState.Zoom);
        }

        if (!on)
        {
            if (_plates.Count > 0)
            {
                var restore = Viewer.GetViewState();
                Viewer.SetPlates(_plates, _plateWidth, _plateHeight);
                Viewer.SetViewCenter(restore.CenterX, restore.CenterY, restore.Zoom);
            }
            return;
        }

        if (!sideBySide)
        {
            ApplyOverlay();
            return;
        }

        var state = Viewer.GetViewState();
        ApplyPane(Viewer, _viewModel.CompareLeft?.Layer);

        if (_viewModel.CompareRightIsExternal)
            ApplyPane(CompareViewer, _viewModel.CompareExternalLayers);
        else
            ApplyPane(CompareViewer, _viewModel.CompareRight?.Layer);

        Viewer.SetViewCenter(state.CenterX, state.CenterY, state.Zoom);
        CompareViewer.SetViewCenter(state.CenterX, state.CenterY, state.Zoom);
    }

    /// <summary>Feed the single-canvas control: two plates, one mode.</summary>
    private void ApplyOverlay()
    {
        var left = _viewModel.CompareLeft?.Layer;

        PlateLayer? right = _viewModel.CompareRightIsExternal
            ? _viewModel.CompareExternalLayers.FirstOrDefault()
            : _viewModel.CompareRight?.Layer;

        CompareOverlay.SetPlates(left, right);
        CompareOverlay.SetMode(_viewModel.ComparePresentation switch
        {
            PlateComparePresentation.Slide => PlateCompareMode.Slide,
            PlateComparePresentation.Blink => PlateCompareMode.Blink,
            PlateComparePresentation.RipVsReference => PlateCompareMode.RipVsReference,
            _ => PlateCompareMode.Difference
        });

        if (_viewModel.ComparePresentation == PlateComparePresentation.RipVsReference)
            CompareOverlay.SetReference(_lastComparison);
    }

    /// <summary>Load a rasterised reference (PDF via Ghostscript, or an image) and diff the plate.</summary>
    private void CompareReference_Click(object sender, RoutedEventArgs e)
    {
        var plate = _viewModel.CompareLeft?.Layer;
        if (plate is null)
        {
            _viewModel.StatusText = "Choose a left plate before comparing with a reference.";
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Reference page (PDF or raster)",
            Filter = "Reference (PDF, TIFF, PNG, JPEG)|*.pdf;*.tif;*.tiff;*.png;*.jpg;*.jpeg|All files|*.*"
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            _viewModel.StatusText = "Rasterising reference...";
            var mask = ReferenceRaster.LoadInkMask(dialog.FileName, plate.Width, plate.Height);
            var comparison = RipReferenceComparer.Compare(
                plate.Coverage, mask, plate.Width, plate.Height, tolerance: 1);

            _lastComparison = comparison;
            _viewModel.ComparePresentation = PlateComparePresentation.RipVsReference;
            CompareOverlay.SetReference(comparison);
            RefreshCompare();

            _viewModel.StatusText = comparison.IsClean
                ? $"Reference match: no RIP errors ({System.IO.Path.GetFileName(dialog.FileName)})"
                : $"RIP vs reference: {comparison.Missing:N0} missing, {comparison.Extra:N0} extra " +
                  $"({comparison.ErrorPercentOfInk:0.00}% of reference ink)";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "Reference comparison",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ApplyPane(PlateViewer viewer, PlateLayer? layer)
    {
        if (layer is null)
        {
            viewer.Clear();
            return;
        }

        ApplyPane(viewer, new[] { layer });
    }

    private void ApplyPane(PlateViewer viewer, IReadOnlyList<PlateLayer> plates)
    {
        if (plates.Count == 0)
        {
            viewer.Clear();
            return;
        }

        var width = plates.Max(p => p.Width);
        var height = plates.Max(p => p.Height);
        viewer.SetPlates(plates, width, height);
        var previewDpi = _viewModel.PreviewDpi > 0
            ? _viewModel.PreviewDpi
            : _viewModel.CurrentJob?.Metadata.Resolution ?? 96;
        viewer.PixelsPerMm = previewDpi / 25.4;
    }

    /// <summary>Mirror one viewer's centre and zoom onto the other, without feedback loops.</summary>
    private void SyncCompare(PlateViewer source, PlateViewer target)
    {
        if (_syncingCompare || !_viewModel.IsCompareMode || !_viewModel.CompareSyncLocked)
            return;

        _syncingCompare = true;
        try
        {
            var state = source.GetViewState();
            target.SetViewCenter(state.CenterX, state.CenterY, state.Zoom);
        }
        finally
        {
            _syncingCompare = false;
        }
    }

    private void UpdateNavigator() => Navigator.SetViewport(Viewer.VisibleImageRect);
    
    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.ActiveTool):
                Viewer.Tool = _viewModel.ActiveTool;
                CompareViewer.Tool = _viewModel.ActiveTool;
                break;
            case nameof(MainViewModel.ViewMode):
                _detailTiles.Clear();
                Viewer.ViewMode = _viewModel.ViewMode;
                CompareViewer.ViewMode = _viewModel.ViewMode;
                break;
            case nameof(MainViewModel.ProgressiveCount):
                _detailTiles.Clear();
                Viewer.ProgressiveCount = _viewModel.ProgressiveCount;
                break;
            case nameof(MainViewModel.Aperture):
                Viewer.ApertureSize = _viewModel.Aperture;
                break;
            case nameof(MainViewModel.ShowContentOverlay):
                Viewer.ShowContentOverlay = _viewModel.ShowContentOverlay;
                break;
            case nameof(MainViewModel.SimulateMetallicPaper):
                _detailTiles.Clear();
                Viewer.SimulateMetallicPaper = _viewModel.SimulateMetallicPaper;
                CompareViewer.SimulateMetallicPaper = _viewModel.SimulateMetallicPaper;
                UpdatePaperColor();
                break;
            case nameof(MainViewModel.UseCustomSubstrate):
            case nameof(MainViewModel.SubstrateColor):
                _detailTiles.Clear();
                UpdatePaperColor();
                break;
            case nameof(MainViewModel.DifferenceThreshold):
                CompareOverlay.DifferenceThreshold = _viewModel.DifferenceThreshold;
                _viewModel.DifferenceSummary = "—";
                break;
            case nameof(MainViewModel.RectangleMeasure):
                Viewer.RectangleMeasure = _viewModel.RectangleMeasure;
                break;
            case nameof(MainViewModel.ShowPreflightPane):
                LeftPanelColumn.Width = _viewModel.ShowPreflightPane ? new GridLength(300) : new GridLength(0);
                LeftPanelBorder.Visibility = _viewModel.ShowPreflightPane ? Visibility.Visible : Visibility.Collapsed;
                break;
            case nameof(MainViewModel.BackSideOpacity):
                Viewer.BackSideOpacity = _viewModel.BackSideOpacity;
                CompareViewer.BackSideOpacity = _viewModel.BackSideOpacity;
                break;
            case nameof(MainViewModel.ShowOriginalPixels):
                if (_viewModel.ShowOriginalPixels)
                    Viewer.ZoomNativeTo(1.0);
                else
                    Viewer.FitToView();
                break;
        }
    }
    
    // ------------------------------------------------------------------------------------------
    // Viewer -> view model
    // ------------------------------------------------------------------------------------------
    
    private void OnViewChanged()
    {
        SyncCompare(Viewer, CompareViewer);
        _viewModel.UpdateViewState(Viewer.NativeZoom);
        var detailFactor = Viewer.ActiveDetailFactor;
        if (Viewer.ShowsNativePixels || detailFactor > 1)
            _viewModel.UpdateViewScale(Viewer.NativeZoom, detailFactor, 0, detailFactor == 1);
        else
            _viewModel.UpdateViewScale(Viewer.NativeZoom, Viewer.NativeFactor, Viewer.CurrentLevelIndex, false);

        var visible = Viewer.VisibleImageRect;
        var ppm = Viewer.PixelsPerMm;
        _viewModel.NavigatorViewportText = visible.IsEmpty || ppm <= 0
            ? "—"
            : $"{visible.Width / ppm:0.0} × {visible.Height / ppm:0.0} mm";
        UpdateRulers();
        UpdateNavigator();
    }
    
    private void UpdateRulers()
    {
        var screenPixelsPerMm = Viewer.PixelsPerMm * Viewer.Zoom;
        
        RulerTop.PixelsPerMm = screenPixelsPerMm;
        RulerTop.Origin = Viewer.OffsetX;
        
        RulerLeft.PixelsPerMm = screenPixelsPerMm;
        RulerLeft.Origin = Viewer.OffsetY;
    }
    
    // ------------------------------------------------------------------------------------------
    // Menu / toolbar handlers
    // ------------------------------------------------------------------------------------------
    
    private void OpenCommand_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        // The dialog and loading logic live in the view model, so the menu, toolbar and Ctrl+O
        // all share one code path.
        _viewModel.OpenFileCommand.Execute(null);
    }
    
    private void RecentFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: RecentFileEntry entry })
            _viewModel.OpenRecentFileCommand.Execute(entry.Path);
    }

    private void RecentPin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: RecentFileEntry entry })
            _viewModel.TogglePinRecentCommand.Execute(entry);
    }
    
    private void ZoomButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }
    
    private void ZoomPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } &&
            double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var zoom))
        {
            _viewModel.RequestZoom(zoom);
        }
    }
    
    /// <summary>
    /// Decode native-resolution pixels for the requested region, composite them, and hand the
    /// bitmap to the viewer. Only TIFF plates expose a native source; other formats stay preview.
    /// </summary>
    private readonly Dictionary<string, TiffStripSource> _sources = new();
    private readonly FolderWatchService _folderWatch = new();
    private readonly DetailTileCache _detailTiles = new();

    private void OnFolderFileDetected(object? sender, string path)
    {
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (!_viewModel.AutoOpenNewFiles || _viewModel.IsLoading)
                return;

            await _viewModel.LoadJobAsync(path);
        }));
    }

    private TiffStripSource? OpenSource(PlateLayer plate)
    {
        var path = plate.Separation.FilePath;
        if (string.IsNullOrEmpty(path))
            return null;

        var key = path + "|" + plate.Separation.PageIndex;
        if (_sources.TryGetValue(key, out var existing))
            return existing;

        try
        {
            var source = TiffStripSource.Open(path, plate.Separation.PageIndex);
            _sources[key] = source;
            return source;
        }
        catch (PlateSourceException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private void DisposeSources()
    {
        foreach (var state in _detailStates.Values)
        {
            var detail = state.Cts;
            state.Cts = null;
            if (detail is null)
                continue;

            try { detail.Cancel(); }
            catch (ObjectDisposedException) { /* already finished */ }
        }

        foreach (var source in _sources.Values)
            source.Dispose();
        _sources.Clear();
    }

    /// <summary>
    /// Decode native pixels for the requested region through IPlateSource: strips are read with
    /// random access, so a 1:1 view costs the visible strips, not the whole page.
    /// </summary>
    private sealed class DetailState
    {
        public CancellationTokenSource? Cts;
        public bool Pending;
        public Rect Region = Rect.Empty;
        public int Generation;
    }

    private readonly Dictionary<PlateViewer, DetailState> _detailStates = new();

    private DetailState DetailStateOf(PlateViewer viewer)
    {
        if (!_detailStates.TryGetValue(viewer, out var state))
            _detailStates[viewer] = state = new DetailState();
        return state;
    }

    private async void OnDetailRequested(object? sender, DetailRequest request)
    {
        var viewer = sender as PlateViewer ?? Viewer;
        var state = DetailStateOf(viewer);
        var panePlates = viewer.Plates;
        // Superseded pan: cancel, but never dispose here. The running task still reads
        // cts.Token; disposing it threw ObjectDisposedException on the UI thread.
        var previous = state.Cts;
        state.Cts = null;

        if (previous is not null)
        {
            try { previous.Cancel(); }
            catch (ObjectDisposedException) { /* already finished */ }
        }

        if (state.Pending && state.Region.Contains(request.Region))
            return;

        var anySolo = panePlates.Any(p => p.Solo);
        var plates = panePlates.Where(p => anySolo ? p.Solo : p.Visible).ToList();

        if (viewer.ViewMode == PlateViewMode.Progressive)
            plates = plates.Take(Math.Max(0, viewer.ProgressiveCount)).ToList();

        if (plates.Count == 0 || viewer.SimulationActive)
        {
            viewer.SetDetail(null, Rect.Empty);
            return;
        }

        var work = new List<(PlateLayer Plate, TiffStripSource Source)>();
        foreach (var plate in plates)
        {
            var source = OpenSource(plate);
            if (source is null)
            {
                viewer.SetDetail(null, Rect.Empty);
                return;
            }

            work.Add((plate, source));
        }

        var nativeFactor = Math.Max(1, viewer.NativeFactor);
        var decimation = Math.Clamp(request.Factor, 1, 4);
        var info = work[0].Source.Info;
        var visible = request.Region;

        // Base-level rect -> native pixels, then snapped to the decimation grid so every
        // output pixel is an exact decimation x decimation block of native pixels.
        var nx0 = (int)Math.Clamp((long)Math.Floor(visible.X) * nativeFactor, 0, info.Width - 1L);
        var ny0 = (int)Math.Clamp((long)Math.Floor(visible.Y) * nativeFactor, 0, info.Height - 1L);
        var nx1 = (int)Math.Clamp((long)Math.Ceiling(visible.Right) * nativeFactor, nx0 + 1L, info.Width);
        var ny1 = (int)Math.Clamp((long)Math.Ceiling(visible.Bottom) * nativeFactor, ny0 + 1L, info.Height);

        var ax = (nx0 / decimation) * decimation;
        var ay = (ny0 / decimation) * decimation;
        var bx = (int)Math.Min(info.Width, ((long)(nx1 + decimation - 1) / decimation) * decimation);
        var by = (int)Math.Min(info.Height, ((long)(ny1 + decimation - 1) / decimation) * decimation);
        var ow = (bx - ax) / decimation;
        var oh = (by - ay) / decimation;

        if (ow < 1 || oh < 1)
        {
            viewer.SetDetail(null, Rect.Empty);
            return;
        }

        const long detailPixelBudget = 32_000_000;
        const long detailByteBudget = 256L * 1024 * 1024;

        var estimatedBytes = (long)ow * oh * 4 +
                             (long)info.RowBytes * (long)oh * decimation * work.Count;
        if ((long)ow * oh > detailPixelBudget || estimatedBytes > detailByteBudget)
        {
            DiagnosticsLog.Info($"detail skipped: {ow}x{oh} dec={decimation} " +
                                $"est={estimatedBytes / (1024 * 1024)}MB at zoom {viewer.NativeZoom:0.###}");
            viewer.SetDetail(null, Rect.Empty);
            return;
        }

        DiagnosticsLog.Info($"detail visible={visible} factor={decimation} zoom={viewer.NativeZoom:0.###}");
        DiagnosticsLog.Info($"detail request L{decimation} {ax},{ay} {ow}x{oh} " +
                            $"dec={decimation} zoom={viewer.NativeZoom:0.###}");

        state.Pending = true;
        state.Region = visible;

        var cts = new CancellationTokenSource();
        state.Cts = cts;
        var generation = ++state.Generation;

        var detailRect = new Rect(ax / (double)nativeFactor, ay / (double)nativeFactor,
                                  ow * (double)decimation / nativeFactor, oh * (double)decimation / nativeFactor);
        var publishClock = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var bitmap = await Task.Run(async () =>
            {
                var buffer = new byte[(long)ow * oh * 4];
                const int tile = DetailTileCache.TileSize;
                var originX = ax / decimation;
                var originY = ay / decimation;
                var sourceKey = work[0].Plate.GetHashCode();
                var decodedTiles = 0;
                var reusedTiles = 0;

                for (var oy = 0; oy < oh; oy += tile)
                {
                    for (var ox = 0; ox < ow; ox += tile)
                    {
                        cts.Token.ThrowIfCancellationRequested();

                        var tw = Math.Min(tile, ow - ox);
                        var th = Math.Min(tile, oh - oy);

                        if (!_detailTiles.TryGet(sourceKey, decimation, originX + ox, originY + oy, out var tileBitmap))
                        {
                            tileBitmap = await RenderDetailTileAsync(
                                work, ax + ox * decimation, ay + oy * decimation,
                                tw, th, decimation, cts.Token).ConfigureAwait(false);
                            _detailTiles.Put(sourceKey, decimation, originX + ox, originY + oy, tileBitmap);
                            decodedTiles++;
                        }
                        else
                        {
                            reusedTiles++;
                        }

                        tileBitmap.CopyPixels(new Int32Rect(0, 0, tw, th), buffer, ow * 4,
                                              ((oy * ow) + ox) * 4);

                        // Paint completed tiles over the low-resolution preview while the rest decode.
                        if (publishClock.ElapsedMilliseconds >= 80)
                        {
                            publishClock.Restart();
                            var snapshotBuffer = (byte[])buffer.Clone();
                            var preview = BitmapSource.Create(ow, oh, 96, 96, PixelFormats.Bgra32,
                                                              null, snapshotBuffer, ow * 4);
                            preview.Freeze();
                            var previewGeneration = generation;
                            var previewRect = detailRect;
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                if (previewGeneration == state.Generation)
                                    viewer.SetDetailProgress(preview, previewRect);
                            }), System.Windows.Threading.DispatcherPriority.Render);
                        }
                    }
                }

                DiagnosticsLog.Info($"detail tiles L{decimation}: {reusedTiles} reused, " +
                                    $"{decodedTiles} decoded at zoom {viewer.NativeZoom:0.###}");

                var result = BitmapSource.Create(ow, oh, 96, 96, PixelFormats.Bgra32, null, buffer, ow * 4);
                result.Freeze();
                return result;
            }, cts.Token).ConfigureAwait(true);

            if (generation != state.Generation)
                return;

            viewer.SetDetail(bitmap, detailRect);
        }
        catch (OperationCanceledException)
        {
            DiagnosticsLog.Info("detail cancelled");
        }
        catch (Exception ex) when (ex is PlateSourceException or IOException
                                       or UnauthorizedAccessException or ArgumentException
                                       or ObjectDisposedException)
        {
            DiagnosticsLog.Error("detail decode failed", ex);
            if (generation == state.Generation)
                viewer.SetDetail(null, Rect.Empty);
        }
        catch (Exception ex)
        {
            // async void: an unhandled exception here terminates the process.
            DiagnosticsLog.Error("detail unexpected failure", ex);
            if (generation == state.Generation)
                viewer.SetDetail(null, Rect.Empty);
        }
        finally
        {
            if (ReferenceEquals(state.Cts, cts))
                state.Cts = null;

            cts.Dispose();

            if (generation == state.Generation)
            {
                state.Pending = false;
                state.Region = Rect.Empty;
            }
        }
    }

    /// <summary>
    /// Composite one tile of the pyramid. <paramref name="decimation"/> is 1 (native),
    /// 2 (1200 dpi) or 4 (600 dpi); each output pixel averages its decimation x decimation
    /// native block so halftone screens do not alias.
    /// </summary>
    private async Task<BitmapSource> RenderDetailTileAsync(
        IReadOnlyList<(PlateLayer Plate, TiffStripSource Source)> work,
        int nativeX, int nativeY, int outW, int outH, int decimation, CancellationToken ct)
    {
        var film = Viewer.ViewMode == PlateViewMode.FilmBlackWhite;
        var (paperR, paperG, paperB) = _viewModel.SubstrateRgb;

        var n = work.Count;
        var rows = new byte[n][];
        var rowBytesArr = new int[n];
        var kr = new float[n];
        var kg = new float[n];
        var kb = new float[n];
        var inverted = new bool[n];
        var isWhite = new bool[n];
        var opacity = new float[n];

        var nativeRows = outH * decimation;

        for (var i = 0; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();

            var (plate, source) = work[i];
            var rb = source.Info.RowBytes;
            rowBytesArr[i] = rb;
            rows[i] = new byte[(long)rb * nativeRows];
            await source.ReadRowsAsync(nativeY, nativeRows, rows[i], ct).ConfigureAwait(false);

            kr[i] = 1f - plate.R / 255f;
            kg[i] = 1f - plate.G / 255f;
            kb[i] = 1f - plate.B / 255f;
            inverted[i] = plate.Inverted;
            opacity[i] = (float)Math.Clamp(plate.Opacity, 0.0, 1.0);
            isWhite[i] = plate.Separation.Color == SeparationColor.White ||
                         plate.Name.Contains("white", StringComparison.OrdinalIgnoreCase);
        }

        var pixels = new float[(long)outW * outH * 3];
        for (var p = 0; p < outW * outH; p++)
        {
            pixels[p * 3] = paperR;
            pixels[p * 3 + 1] = paperG;
            pixels[p * 3 + 2] = paperB;
        }

        var block = decimation * decimation;
        var invBlock = 1f / block;
        long sampleCount = 0;
        long inkCount = 0;

        for (var i = 0; i < n; i++)
        {
            var row = rows[i];
            var rowBytes = rowBytesArr[i];

            for (var py = 0; py < outH; py++)
            {
                if ((py & 31) == 0)
                    ct.ThrowIfCancellationRequested();

                var blockRow = py * decimation;

                for (var px = 0; px < outW; px++)
                {
                    var sum = 0;
                    var blockX = nativeX + px * decimation; // tiles start at nativeX, not row 0

                    for (var dy = 0; dy < decimation; dy++)
                    {
                        var offset = (long)(blockRow + dy) * rowBytes;
                        for (var dx = 0; dx < decimation; dx++)
                        {
                            var xx = blockX + dx;
                            sum += (row[offset + (xx >> 3)] >> (7 - (xx & 7))) & 1;
                        }
                    }

                    var cov = sum * invBlock;
                    if (inverted[i]) cov = 1f - cov;
                    cov *= opacity[i];
                    if (i == 0)
                    {
                        sampleCount++;
                        if (cov > 0f) inkCount++;
                    }
                    if (cov <= 0f) continue;

                    var p = ((long)py * outW + px) * 3;

                    if (isWhite[i])
                    {
                        pixels[p] += (1f - pixels[p]) * cov;
                        pixels[p + 1] += (1f - pixels[p + 1]) * cov;
                        pixels[p + 2] += (1f - pixels[p + 2]) * cov;
                        continue;
                    }

                    if (film)
                    {
                        var t = 1f - cov;
                        pixels[p] *= t;
                        pixels[p + 1] *= t;
                        pixels[p + 2] *= t;
                    }
                    else
                    {
                        pixels[p] *= 1f - cov * kr[i];
                        pixels[p + 1] *= 1f - cov * kg[i];
                        pixels[p + 2] *= 1f - cov * kb[i];
                    }
                }
            }
        }

        var buffer = new byte[(long)outW * outH * 4];
        for (var p = 0; p < outW * outH; p++)
        {
            buffer[p * 4] = (byte)(pixels[p * 3 + 2] * 255f + 0.5f);
            buffer[p * 4 + 1] = (byte)(pixels[p * 3 + 1] * 255f + 0.5f);
            buffer[p * 4 + 2] = (byte)(pixels[p * 3] * 255f + 0.5f);
            buffer[p * 4 + 3] = 255;
        }

        var tileBitmap = BitmapSource.Create(outW, outH, 96, 96, PixelFormats.Bgra32, null, buffer, outW * 4);
        tileBitmap.Freeze();
        return tileBitmap;
    }

    private void UpdatePaperColor()
    {
        var (r, g, b) = _viewModel.SubstrateRgb;
        var color = Color.FromRgb((byte)(r * 255f + 0.5f), (byte)(g * 255f + 0.5f), (byte)(b * 255f + 0.5f));
        Viewer.PaperColor = color;
        CompareViewer.PaperColor = color;
    }

    private void SubstrateColor_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ColorPickerWindow(_viewModel.SubstrateColor) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _viewModel.SubstrateColor = dialog.SelectedColor;
            _viewModel.UseCustomSubstrate = true;
        }
    }

    private void Viewer_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(Viewer);
        var maxX = Math.Max(8, Viewer.ActualWidth - HoverTip.ActualWidth - 10);
        var maxY = Math.Max(8, Viewer.ActualHeight - HoverTip.ActualHeight - 10);

        Canvas.SetLeft(HoverTip, Math.Clamp(p.X + 16, 8, maxX));
        Canvas.SetTop(HoverTip, Math.Clamp(p.Y + 20, 8, maxY));
    }

    private void SeparationCustomColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SeparationViewModel separation })
            return;

        var initial = separation.CustomColor
            ?? (separation.DisplayBrush as SolidColorBrush)?.Color
            ?? Colors.Gray;

        var dialog = new ColorPickerWindow(initial) { Owner = this };
        if (dialog.ShowDialog() == true)
            separation.SetCustomColor(dialog.SelectedColor);
    }

    private void SeparationResetColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SeparationViewModel separation })
            separation.ResetColor();
    }

    private void PressProfileBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        _viewModel.CommitPressProfileName(PressProfileBox.Text);
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void QueueList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (QueueList?.SelectedItem is SoftProof.Core.Services.RipQueueEntry entry)
            _viewModel.OpenQueueEntryCommand.Execute(entry);
    }

    private void QueueList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = ItemsControl.ContainerFromElement(QueueList, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (item is { IsSelected: false })
        {
            QueueList.SelectedItems.Clear();
            item.IsSelected = true;
        }
    }

    private async void QueueOpenSelected_Click(object sender, RoutedEventArgs e)
    {
        var entries = QueueList.SelectedItems
            .OfType<SoftProof.Core.Services.RipQueueEntry>()
            .DistinctBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (entries.Length == 0)
            return;

        // Files may come from different jobs (different base names): open every selected file
        // as separations of one job. Each file keeps its own resolved name/colour.
        var paths = entries.Select(entry => entry.Path).ToArray();
        await _viewModel.LoadJobsAsync(paths);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Viewer.Refresh();
    
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    
    private void About_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "SoftProof\n\n" +
            "Soft-proofing workspace for RIP separations.\n" +
            "Real RIP file loading is not implemented yet; File > Open Demo Job loads a sample 8-plate job.\n\n" +
            "Shortcuts: V select, H pan, D densitometer, M caliper, 1 = 100%, Ctrl+0 fit, Ctrl +/- zoom, " +
            "mouse wheel zoom, Space+drag or middle mouse to pan.",
            "About SoftProof", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    
    // ------------------------------------------------------------------------------------------
    // Keyboard shortcuts
    // ------------------------------------------------------------------------------------------
    
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox)
            return;
        
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.D0:
                case Key.NumPad0:
                    _viewModel.FitToWindowCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.OemPlus:
                case Key.Add:
                    _viewModel.ZoomInCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.OemMinus:
                case Key.Subtract:
                    _viewModel.ZoomOutCommand.Execute(null);
                    e.Handled = true;
                    break;
            }
            return;
        }
        
        if (Keyboard.Modifiers != ModifierKeys.None)
            return;
        
        switch (e.Key)
        {
            case Key.V:
                _viewModel.SetToolCommand.Execute(ViewerTool.Select);
                e.Handled = true;
                break;
            case Key.H:
                _viewModel.SetToolCommand.Execute(ViewerTool.Pan);
                e.Handled = true;
                break;
            case Key.D:
                _viewModel.SetToolCommand.Execute(ViewerTool.Densitometer);
                e.Handled = true;
                break;
            case Key.M:
                _viewModel.SetToolCommand.Execute(ViewerTool.Measure);
                e.Handled = true;
                break;
            case Key.D1:
            case Key.NumPad1:
                _viewModel.ZoomActualSizeCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }
}
