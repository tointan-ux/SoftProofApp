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
    private int _detailGeneration;
    private (double CenterX, double CenterY, double Zoom)? _pendingView;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        
        // View model -> viewer
        _viewModel.PlatesLoaded += OnPlatesLoaded;
        _viewModel.PlatesChanged += (_, _) => Viewer.Refresh();
        _viewModel.ZoomRequested += (_, zoom) => Viewer.SetNativeZoom(zoom);
        _viewModel.FitToWindowRequested += (_, _) => Viewer.FitToView();
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        
        // Viewer -> view model
        Viewer.ViewChanged += (_, _) => OnViewChanged();
        Viewer.CursorMoved += (_, point) => _viewModel.OnCursorMoved(point);
        Viewer.RectangleChanged += (_, e) => _viewModel.OnRectangleMeasured(e.Bounds, e.Completed);
        CompareViewer.CursorMoved += (_, point) => _viewModel.OnCursorMoved(point);
        Viewer.CaliperChanged += (_, args) => _viewModel.OnCaliperChanged(args.Start, args.End);

        // Navigator: drag the viewport to pan; the viewer keeps its own zoom.
        Navigator.CenterRequested += (_, centre) => Viewer.SetViewCenter(centre.X, centre.Y);
        Viewer.DetailRequested += OnDetailRequested;

        // Auto-refresh when the RIP rewrites the open file.
        _watch.FileChanged += OnFileRewrittenByRip;

        Viewer.Tool = _viewModel.ActiveTool;
        Viewer.ViewMode = _viewModel.ViewMode;
        Viewer.ShowContentOverlay = _viewModel.ShowContentOverlay;
        Viewer.BlackOverprint = _viewModel.BlackOverprint;
        Viewer.SimulateMetallicPaper = _viewModel.SimulateMetallicPaper;
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
                break;
            case nameof(MainViewModel.ViewMode):
                Viewer.ViewMode = _viewModel.ViewMode;
                break;
            case nameof(MainViewModel.ProgressiveCount):
                Viewer.ProgressiveCount = _viewModel.ProgressiveCount;
                break;
            case nameof(MainViewModel.Aperture):
                Viewer.ApertureSize = _viewModel.Aperture;
                break;
            case nameof(MainViewModel.ShowContentOverlay):
                Viewer.ShowContentOverlay = _viewModel.ShowContentOverlay;
                break;
            case nameof(MainViewModel.BlackOverprint):
                Viewer.BlackOverprint = _viewModel.BlackOverprint;
                CompareViewer.BlackOverprint = _viewModel.BlackOverprint;
                break;
            case nameof(MainViewModel.SimulateMetallicPaper):
                Viewer.SimulateMetallicPaper = _viewModel.SimulateMetallicPaper;
                CompareViewer.SimulateMetallicPaper = _viewModel.SimulateMetallicPaper;
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
                    Viewer.SetNativeZoom(1.0);
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
        _viewModel.UpdateViewScale(Viewer.NativeZoom, Viewer.NativeFactor, Viewer.CurrentLevelIndex, Viewer.ShowsNativePixels);

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
        foreach (var source in _sources.Values)
            source.Dispose();
        _sources.Clear();
    }

    /// <summary>
    /// Decode native pixels for the requested region through IPlateSource: strips are read with
    /// random access, so a 1:1 view costs the visible strips, not the whole page.
    /// </summary>
    private async void OnDetailRequested(object? sender, Rect region)
    {
        _detailCts?.Cancel();
        _detailCts?.Dispose();
        _detailCts = null;

        var plates = _plates.ToList();
        if (plates.Count == 0 || Viewer.SimulationActive)
        {
            Viewer.SetDetail(null, Rect.Empty);
            return;
        }

        // Resolve sources on the UI thread so the worker only reads them.
        var work = new List<(PlateLayer Plate, TiffStripSource Source)>();
        foreach (var plate in plates)
        {
            var source = OpenSource(plate);
            if (source is null)
            {
                Viewer.SetDetail(null, Rect.Empty);
                return;
            }

            work.Add((plate, source));
        }

        var factor = Math.Max(1, Viewer.NativeFactor);
        var x = Math.Max(0, (int)Math.Floor(region.X)) * factor;
        var y = Math.Max(0, (int)Math.Floor(region.Y)) * factor;
        var w = Math.Max(1, (int)Math.Ceiling(region.Width)) * factor;
        var h = Math.Max(1, (int)Math.Ceiling(region.Height)) * factor;

        var cts = new CancellationTokenSource();
        _detailCts = cts;
        var generation = ++_detailGeneration;

        try
        {
            var bitmap = await Task.Run(async () =>
            {
                var buffer = new byte[(long)w * h * 4];
                for (var i = 0; i < buffer.Length; i += 4)
                {
                    buffer[i] = 255; buffer[i + 1] = 255; buffer[i + 2] = 255; buffer[i + 3] = 255;
                }

                foreach (var (plate, source) in work)
                {
                    cts.Token.ThrowIfCancellationRequested();

                    var rowBytes = source.Info.RowBytes;
                    var rows = new byte[(long)rowBytes * h];
                    await source.ReadRowsAsync(y, h, rows, cts.Token).ConfigureAwait(false);

                    var kr = 1f - plate.R / 255f;
                    var kg = 1f - plate.G / 255f;
                    var kb = 1f - plate.B / 255f;
                    var inverted = plate.Inverted;

                    for (var row = 0; row < h; row++)
                    {
                        if ((row & 63) == 0)
                            cts.Token.ThrowIfCancellationRequested();

                        var rowOffset = row * rowBytes;
                        for (var col = 0; col < w; col++)
                        {
                            var pixelX = x + col;
                            var bit = (rows[rowOffset + (pixelX >> 3)] >> (7 - (pixelX & 7))) & 1;
                            var cov = bit == 1 ? 1f : 0f;
                            if (inverted) cov = 1f - cov;
                            if (cov <= 0f) continue;

                            var o = (row * w + col) * 4;
                            buffer[o] = (byte)(buffer[o] * (1f - cov * kb));
                            buffer[o + 1] = (byte)(buffer[o + 1] * (1f - cov * kg));
                            buffer[o + 2] = (byte)(buffer[o + 2] * (1f - cov * kr));
                        }
                    }
                }

                var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, buffer, w * 4);
                result.Freeze();
                return result;
            }, cts.Token).ConfigureAwait(true);

            if (generation != _detailGeneration)
                return;

            Viewer.SetDetail(bitmap, new Rect(x / (double)factor, y / (double)factor,
                                              w / (double)factor, h / (double)factor));
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer view
        }
        catch (Exception ex) when (ex is PlateSourceException or IOException
                                       or UnauthorizedAccessException or ArgumentException)
        {
            if (generation == _detailGeneration)
                Viewer.SetDetail(null, Rect.Empty);
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

        // One job is open at a time: if the selection spans groups, open the first job only.
        var groups = entries.GroupBy(entry => entry.JobName).ToArray();
        var chosen = groups[0].ToArray();

        await _viewModel.LoadJobsAsync(chosen.Select(entry => entry.Path).ToArray());

        if (groups.Length > 1)
            _viewModel.StatusText =
                $"Opened {chosen.Length} plate(s) of {groups[0].Key}. " +
                $"Selection spanned {groups.Length} jobs; only one job can be open at a time.";
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
