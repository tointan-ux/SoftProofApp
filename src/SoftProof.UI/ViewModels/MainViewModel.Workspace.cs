using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoftProof.Core.Analysis;
using SoftProof.Core.Models;
using SoftProof.UI.Services;
using SoftProof.Viewer;

namespace SoftProof.UI.ViewModels;

/// <summary>
/// Workspace state: active tool, view mode, plate list actions, cursor / densitometer readouts,
/// caliper, preflight results and sign-off.
/// </summary>
public partial class MainViewModel
{
    private List<PlateLayer> _layers = new();
    private bool _suspendPlatesChanged;
    
    // ---- tools & view -----------------------------------------------------------------------
    
    [ObservableProperty]
    private ViewerTool _activeTool = ViewerTool.Select;
    
    [ObservableProperty]
    private PlateViewMode _viewMode = PlateViewMode.Composite;
    
    [ObservableProperty]
    private int _progressiveCount = 1;
    
    [ObservableProperty]
    private int _progressiveMax = 1;
    
    /// <summary>Densitometer aperture in pixels: 1, 3 or 5.</summary>
    [ObservableProperty]
    private int _aperture = 1;
    
    [ObservableProperty]
    private bool _showDieline = true;
    
    /// <summary>True when the current job has raster data to display.</summary>
    [ObservableProperty]
    private bool _hasRaster;
    
    public string ZoomText => $"{ZoomLevel * 100:0}%";
    
    partial void OnZoomLevelChanged(double value) => OnPropertyChanged(nameof(ZoomText));
    
    // ---- cursor, densitometer, caliper --------------------------------------------------------
    
    [ObservableProperty]
    private string _cursorXText = "--";
    
    [ObservableProperty]
    private string _cursorYText = "--";
    
    [ObservableProperty]
    private string _caliperText = string.Empty;
    
    [ObservableProperty]
    private string _caliperDetail = string.Empty;
    
    [ObservableProperty]
    private bool _hasCaliper;
    
    public ObservableCollectionEx<DensityReading> DensityReadings { get; } = new();
    
    // ---- preflight ---------------------------------------------------------------------------
    
    public ObservableCollectionEx<PreflightItem> PreflightChecks { get; } = new();
    
    [ObservableProperty]
    private string _preflightSummary = "No job loaded";
    
    // ---- header / window texts -----------------------------------------------------------------
    
    public bool HasJob => CurrentJob != null;
    
    public string WindowTitle => CurrentJob == null
        ? "SoftProof"
        : $"SoftProof  |  {CurrentJob.Name}  |  {CurrentJob.Metadata.Resolution} DPI";
    
    public string PressText => string.IsNullOrWhiteSpace(CurrentJob?.Metadata.PressName)
        ? "--" : CurrentJob!.Metadata.PressName;
    
    public string ResolutionText => CurrentJob == null ? "--" : $"{CurrentJob.Metadata.Resolution} DPI";
    
    public string EngineText => CurrentJob == null || string.IsNullOrWhiteSpace(CurrentJob.Metadata.RipName)
        ? "--" : $"{CurrentJob.Metadata.RipName} {CurrentJob.Metadata.RipVersion}".Trim();
    
    public string ScreeningText => CurrentJob == null
        ? "--"
        : $"{CurrentJob.ProofSettings.ScreeningType} ({CurrentJob.ProofSettings.ScreenFrequency:0} lpi)";
    
    public string PlatesTitle => $"Separations & Plates ({Separations.Count})";
    
    public bool IsSignedOff => JobStatus == JobStatus.Approved;
    
    private double PixelsPerMm =>
        CurrentJob is { Metadata.Resolution: > 0 } job ? job.Metadata.Resolution / 25.4 : 10.0;
    
    partial void OnCurrentJobChanged(PrintJob? value)
    {
        OnPropertyChanged(nameof(HasJob));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(PressText));
        OnPropertyChanged(nameof(ResolutionText));
        OnPropertyChanged(nameof(EngineText));
        OnPropertyChanged(nameof(ScreeningText));
        SignOffCommand.NotifyCanExecuteChanged();
    }
    
    partial void OnJobStatusChanged(JobStatus value)
    {
        OnPropertyChanged(nameof(IsSignedOff));
        SignOffCommand.NotifyCanExecuteChanged();
    }
    
    partial void OnActiveToolChanged(ViewerTool value)
    {
        StatusText = value switch
        {
            ViewerTool.Pan => "Pan: drag to move the view",
            ViewerTool.Densitometer => "Densitometer: move over the page to read plate coverage",
            ViewerTool.Measure => "Caliper: click and drag to measure in mm",
            _ => StatusText
        };
    }
    
    partial void OnShowDielineChanged(bool value)
    {
        BatchUpdate(() =>
        {
            foreach (var plate in Separations.Where(s => s.IsTechnical))
                plate.IsVisible = value;
        });
    }
    
    // ---- events to the view --------------------------------------------------------------------
    
    /// <summary>A new set of plates was loaded (or cleared: empty list).</summary>
    public event EventHandler<PlateSetEventArgs>? PlatesLoaded;
    
    /// <summary>Visible / solo / invert flags changed on one or more plates.</summary>
    public event EventHandler? PlatesChanged;
    
    /// <summary>Asks the viewer to zoom (1.0 = 100%).</summary>
    public event EventHandler<double>? ZoomRequested;
    
    public void RequestZoom(double zoom) => ZoomRequested?.Invoke(this, zoom);
    
    /// <summary>Called by the view when the viewer's zoom changed.</summary>
    public void UpdateViewState(double zoom) => ZoomLevel = zoom;
    
    private void NotifyPlatesChanged()
    {
        if (_suspendPlatesChanged) return;
        PlatesChanged?.Invoke(this, EventArgs.Empty);
    }
    
    private void BatchUpdate(Action action)
    {
        _suspendPlatesChanged = true;
        try { action(); }
        finally { _suspendPlatesChanged = false; }
        NotifyPlatesChanged();
    }
    
    // ---- loading ---------------------------------------------------------------------------------
    
    [RelayCommand]
    private async Task OpenDemoJobAsync()
    {
        if (JobStatus == JobStatus.Loading) return;
        
        try
        {
            JobStatus = JobStatus.Loading;
            StatusText = "Generating demo job...";
            
            var demo = await Task.Run(DemoJobFactory.Create);
            StatusText = "Running preflight...";
            var results = await Task.Run(() => PreflightAnalyzer.Run(demo.Job, demo.Plates));
            
            ApplyJob(demo.Job, demo.Plates);
            SetPreflight(results);
            
            JobStatus = JobStatus.Ready;
            StatusText = $"Loaded demo job: {demo.Job.Separations.Count} plates, " +
                         $"{demo.Job.Metadata.Width} x {demo.Job.Metadata.Height} px at {demo.Job.Metadata.Resolution} DPI";
        }
        catch (Exception ex)
        {
            JobStatus = JobStatus.Error;
            StatusText = $"Error: {ex.Message}";
        }
    }
    
    private void ApplyJob(PrintJob job, IReadOnlyList<PlateLayer> plates)
    {
        CurrentJob = job;
        _layers = plates.ToList();
        
        Separations.Clear();
        DensityReadings.Clear();
        foreach (var sep in job.Separations)
        {
            var layer = _layers.FirstOrDefault(l => ReferenceEquals(l.Separation, sep));
            var vm = new SeparationViewModel(sep, layer) { FlagsChanged = NotifyPlatesChanged };
            Separations.Add(vm);
            
            if (layer != null)
                DensityReadings.Add(new DensityReading(sep.Name, vm.DisplayBrush));
        }
        
        HasRaster = _layers.Count > 0;
        ProgressiveMax = Math.Max(1, _layers.Count);
        ProgressiveCount = ProgressiveMax;
        ViewMode = PlateViewMode.Composite;
        ShowDieline = true;
        HasCaliper = false;
        CursorXText = CursorYText = "--";
        OnPropertyChanged(nameof(PlatesTitle));
        
        PlatesLoaded?.Invoke(this,
            new PlateSetEventArgs(_layers, job.Metadata.Width, job.Metadata.Height, PixelsPerMm));
    }
    
    /// <summary>Show a job's plate list when no raster could be loaded for it.</summary>
    private void ShowJobWithoutRaster(PrintJob job)
    {
        ApplyJob(job, Array.Empty<PlateLayer>());
        SetPreflight(PreflightAnalyzer.Run(job, Array.Empty<PlateLayer>()));
    }
    
    private void SetPreflight(IReadOnlyList<PreflightResult> results)
    {
        PreflightChecks.ReplaceAll(results.Select(r => new PreflightItem(r.Title, r.Detail, r.Status)));
        
        var pass = results.Count(r => r.Status == PreflightStatus.Pass);
        var warn = results.Count(r => r.Status == PreflightStatus.Warning);
        var fail = results.Count(r => r.Status == PreflightStatus.Fail);
        var notRun = results.Count(r => r.Status == PreflightStatus.NotRun);
        PreflightSummary = $"{pass} pass · {warn} warn · {fail} fail · {notRun} not run";
    }
    
    // ---- tools -----------------------------------------------------------------------------------
    
    [RelayCommand]
    private void SetTool(ViewerTool tool) => ActiveTool = tool;
    
    /// <summary>Cursor moved over the page; <paramref name="imagePoint"/> is in plate pixels (null = left the page).</summary>
    public void OnCursorMoved(System.Windows.Point? imagePoint)
    {
        if (imagePoint is not { } p || CurrentJob == null)
        {
            CursorXText = CursorYText = "--";
            return;
        }
        
        var ppm = PixelsPerMm;
        CursorXText = $"{p.X / ppm:0.00}";
        CursorYText = $"{p.Y / ppm:0.00}";
        
        var px = (int)Math.Floor(p.X);
        var py = (int)Math.Floor(p.Y);
        var index = 0;
        foreach (var sep in Separations)
        {
            if (sep.Layer == null) continue;
            if (index < DensityReadings.Count)
                DensityReadings[index].Percent = sep.Layer.SamplePercent(px, py, Aperture);
            index++;
        }
    }
    
    /// <summary>Caliper dragged in the viewer; points are in plate pixels.</summary>
    public void OnCaliperChanged(System.Windows.Point start, System.Windows.Point end)
    {
        var ppm = PixelsPerMm;
        var dx = (end.X - start.X) / ppm;
        var dy = (end.Y - start.Y) / ppm;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        var angle = Math.Atan2(-dy, dx) * 180.0 / Math.PI;
        
        CaliperText = $"{distance:0.00} mm";
        CaliperDetail = $"Δx {Math.Abs(dx):0.00} mm   Δy {Math.Abs(dy):0.00} mm   {angle:0.0}°";
        HasCaliper = true;
    }
    
    // ---- plate list quick sets ---------------------------------------------------------------------
    
    private static bool IsProcess(SeparationColor c) =>
        c is SeparationColor.ProcessCyan or SeparationColor.ProcessMagenta
          or SeparationColor.ProcessYellow or SeparationColor.ProcessBlack;
    
    [RelayCommand]
    private void ShowAllPlates() => BatchUpdate(() =>
    {
        foreach (var s in Separations)
        {
            s.IsSolo = false;
            s.IsVisible = true;
        }
    });
    
    [RelayCommand]
    private void ShowCmykOnly() => BatchUpdate(() =>
    {
        foreach (var s in Separations)
        {
            s.IsSolo = false;
            s.IsVisible = IsProcess(s.Color);
        }
    });
    
    [RelayCommand]
    private void ShowSpotsOnly() => BatchUpdate(() =>
    {
        foreach (var s in Separations)
        {
            s.IsSolo = false;
            s.IsVisible = s.IsSpotColor;
        }
    });
    
    [RelayCommand]
    private void ClearSolo() => BatchUpdate(() =>
    {
        foreach (var s in Separations)
            s.IsSolo = false;
    });
    
    [RelayCommand]
    private async Task RunPreflightAsync()
    {
        if (CurrentJob == null) return;
        var job = CurrentJob;
        var layers = _layers.ToList();
        StatusText = "Running preflight...";
        var results = await Task.Run(() => PreflightAnalyzer.Run(job, layers));
        SetPreflight(results);
        StatusText = $"Preflight: {PreflightSummary}";
    }
    
    // ---- sign-off ----------------------------------------------------------------------------------
    
    private bool CanSignOff() =>
        CurrentJob != null && JobStatus != JobStatus.Approved && JobStatus != JobStatus.Loading;
    
    [RelayCommand(CanExecute = nameof(CanSignOff))]
    private void SignOff()
    {
        if (CurrentJob == null) return;
        
        var failed = PreflightChecks.Where(c => c.Status == PreflightStatus.Fail).Select(c => c.Title).ToList();
        if (failed.Count > 0)
        {
            StatusText = "Sign-off blocked, preflight failed: " + string.Join(", ", failed);
            return;
        }
        
        JobStatus = JobStatus.Approved;
        StatusText = $"Signed off by {Environment.UserName} at {DateTime.Now:g}";
        
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoftProof");
            Directory.CreateDirectory(dir);
            var line = System.Text.Json.JsonSerializer.Serialize(new
            {
                job = CurrentJob.Name,
                user = Environment.UserName,
                time = DateTimeOffset.Now,
                preflight = PreflightSummary
            });
            File.AppendAllText(Path.Combine(dir, "signoff-log.jsonl"), line + Environment.NewLine);
        }
        catch
        {
            // The sign-off itself succeeded; failing to write the log must not undo it.
        }
    }
    
    // ---- view-mode helpers ---------------------------------------------------------------------------
    
    partial void OnProgressiveCountChanged(int value)
    {
        if (ViewMode == PlateViewMode.Progressive)
            StatusText = $"Progressive: first {value} of {ProgressiveMax} plates";
    }
}
