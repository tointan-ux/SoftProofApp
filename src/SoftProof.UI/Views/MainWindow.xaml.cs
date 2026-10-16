using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using SoftProof.UI.ViewModels;
using SoftProof.Viewer;

namespace SoftProof.UI.Views;

/// <summary>
/// Main window. Wires the plate viewer and rulers to the view model; all logic lives in the view model.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        
        // View model -> viewer
        _viewModel.PlatesLoaded += OnPlatesLoaded;
        _viewModel.PlatesChanged += (_, _) => Viewer.Refresh();
        _viewModel.ZoomRequested += (_, zoom) => Viewer.SetZoom(zoom);
        _viewModel.FitToWindowRequested += (_, _) => Viewer.FitToView();
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        
        // Viewer -> view model
        Viewer.ViewChanged += (_, _) => OnViewChanged();
        Viewer.CursorMoved += (_, point) => _viewModel.OnCursorMoved(point);
        Viewer.CaliperChanged += (_, args) => _viewModel.OnCaliperChanged(args.Start, args.End);
        
        Viewer.Tool = _viewModel.ActiveTool;
        Viewer.ViewMode = _viewModel.ViewMode;
        Viewer.ApertureSize = _viewModel.Aperture;
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
        }
        else
        {
            Viewer.PixelsPerMm = e.PixelsPerMm;
            Viewer.SetPlates(e.Plates, e.Width, e.Height);
        }
        
        UpdateRulers();
    }
    
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
        }
    }
    
    // ------------------------------------------------------------------------------------------
    // Viewer -> view model
    // ------------------------------------------------------------------------------------------
    
    private void OnViewChanged()
    {
        _viewModel.UpdateViewState(Viewer.Zoom);
        UpdateRulers();
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
        if (sender is MenuItem { DataContext: string path })
            _viewModel.OpenRecentFileCommand.Execute(path);
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
