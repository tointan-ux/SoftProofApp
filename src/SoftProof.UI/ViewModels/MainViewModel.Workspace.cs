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
    
    private ViewTransform? _view;

    /// <summary>
    /// Camera behind the zoom readout. Set by whichever surface is driving the view
    /// (main canvas or the single-canvas compare overlay); null only before the first
    /// view change, when the readout falls back to <see cref="ZoomLevel"/>.
    /// </summary>
    public ViewTransform? View
    {
        get => _view;
        private set
        {
            if (Nullable.Equals(_view, value))
                return;

            _view = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ZoomText));
        }
    }

    public string ZoomText => View?.ZoomText ?? $"{ZoomLevel * 100:0}%";

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

    /// <summary>False = line caliper; true = rectangle (width × height, area, perimeter).</summary>
    [ObservableProperty]
    private bool _rectangleMeasure;

    [ObservableProperty]
    private string _hoverPercentText = string.Empty;

    public bool HasHoverReading => !string.IsNullOrWhiteSpace(HoverPercentText);

    partial void OnHoverPercentTextChanged(string value) =>
        OnPropertyChanged(nameof(HasHoverReading));
    
    // ---- preflight ---------------------------------------------------------------------------
    
    public ObservableCollectionEx<PreflightItem> PreflightChecks { get; } = new();

    /// <summary>Failures, warnings and not-run checks, most severe first. Passes are summarised.</summary>
    public ObservableCollectionEx<PreflightItem> PreflightIssues { get; } = new();

    [ObservableProperty]
    private string _preflightPassedSummary = string.Empty;

    [ObservableProperty]
    private bool _hasPreflightPasses;
    
    [ObservableProperty]
    private string _preflightSummary = "No job loaded";
    
    // ---- header / window texts -----------------------------------------------------------------
    
    public bool HasJob => CurrentJob != null;
    
    public string WindowTitle => CurrentJob == null
        ? "SoftProof"
        : $"SoftProof  |  {CurrentJob.Name}  |  {CurrentJob.Metadata.Resolution} DPI";
    
    public string PressText => string.IsNullOrWhiteSpace(CurrentJob?.Metadata.PressName)
        ? "--" : CurrentJob!.Metadata.PressName;

    // ---- press profiles ------------------------------------------------------------------------

    public ObservableCollectionEx<PressProfile> PressProfiles { get; } = new();

    [ObservableProperty]
    private PressProfile? _selectedPressProfile;

    [ObservableProperty]
    private string _pressProfileName = string.Empty;

    partial void OnSelectedPressProfileChanged(PressProfile? value)
    {
        if (value is null)
            return;

        PressProfileName = value.Name;
        PressProfileStore.Copy(value.Settings, ProofSettings);

        if (CurrentJob is not null)
        {
            PressProfileStore.Copy(value.Settings, CurrentJob.ProofSettings);
            CurrentJob.Metadata.PressName = value.Name;
            OnPropertyChanged(nameof(PressText));
        }

        SavePressProfiles();
    }

    private void LoadPressProfiles()
    {
        var file = PressProfileStore.Load();

        if (file.Profiles.Count == 0)
        {
            var seed = new PressProfile
            {
                Name = "Default press",
                Settings = PressProfileStore.Clone(ProofSettings)
            };
            seed.Settings.Preflight.PressName = seed.Name;
            file = new PressProfileFile { Selected = seed.Name, Profiles = { seed } };
            PressProfileStore.Save(file);
        }

        PressProfiles.ReplaceAll(file.Profiles);

        var selected = PressProfiles.FirstOrDefault(p =>
                           p.Name.Equals(file.Selected, StringComparison.OrdinalIgnoreCase))
                       ?? PressProfiles[0];

        SelectedPressProfile = null;
        SelectedPressProfile = selected;
    }

    /// <summary>Pick a profile by name, or create one from the current settings when the name is new.</summary>
    public void CommitPressProfileName(string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name))
            return;

        var existing = PressProfiles.FirstOrDefault(p =>
            p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            SelectedPressProfile = existing;
            PressProfileName = existing.Name;
            return;
        }

        var profile = new PressProfile
        {
            Name = name,
            Settings = PressProfileStore.Clone(ProofSettings)
        };
        profile.Settings.Preflight.PressName = name;

        PressProfiles.Add(profile);
        SelectedPressProfile = profile;
        PressProfileName = name;
    }

    /// <summary>Snapshot the current (possibly just edited) settings back onto the selected profile.</summary>
    public void SaveCurrentPressProfile()
    {
        if (SelectedPressProfile is null)
            return;

        SelectedPressProfile.Settings = PressProfileStore.Clone(ProofSettings);
        SelectedPressProfile.Settings.Preflight.PressName = SelectedPressProfile.Name;
        SavePressProfiles();
    }

    private void SyncPressProfileFromJob()
    {
        var name = CurrentJob?.Metadata.PressName;
        if (string.IsNullOrWhiteSpace(name))
            return;

        var profile = PressProfiles.FirstOrDefault(p =>
            p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (profile is null)
        {
            profile = new PressProfile
            {
                Name = name,
                Settings = PressProfileStore.Clone(ProofSettings)
            };
            profile.Settings.Preflight.PressName = name;
            PressProfiles.Add(profile);
        }

        if (!ReferenceEquals(profile, SelectedPressProfile))
            SelectedPressProfile = profile;
    }

    private void SavePressProfiles()
    {
        PressProfileStore.Save(new PressProfileFile
        {
            Selected = SelectedPressProfile?.Name ?? string.Empty,
            Profiles = PressProfiles.ToList()
        });
    }
    
    public string ResolutionText => CurrentJob == null ? "--" : $"{CurrentJob.Metadata.Resolution} DPI";
    
    public string EngineText => CurrentJob == null || string.IsNullOrWhiteSpace(CurrentJob.Metadata.RipName)
        ? "--" : $"{CurrentJob.Metadata.RipName} {CurrentJob.Metadata.RipVersion}".Trim();
    
    public string ScreeningText => CurrentJob == null
        ? "--"
        : $"{CurrentJob.ProofSettings.ScreeningType} ({CurrentJob.ProofSettings.ScreenFrequency:0} lpi)";
    
    public string PlatesTitle => $"Separations & Plates ({Separations.Count})";
    
    public bool IsSignedOff => JobStatus == JobStatus.Approved;
    
    private double PixelsPerMm =>
        PreviewDpi > 0 ? PreviewDpi / 25.4
        : CurrentJob is { Metadata.Resolution: > 0 } job ? job.Metadata.Resolution / 25.4 : 10.0;
    
    partial void OnCurrentJobChanged(PrintJob? value)
    {
        OnPropertyChanged(nameof(HasJob));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(PressText));
        SyncPressProfileFromJob();
        OnPropertyChanged(nameof(ResolutionText));
        OnPropertyChanged(nameof(EngineText));
        OnPropertyChanged(nameof(ScreeningText));
        OnPropertyChanged(nameof(SignOffBlockReason));
        SignOffCommand.NotifyCanExecuteChanged();
    }
    
    partial void OnJobStatusChanged(JobStatus value)
    {
        OnPropertyChanged(nameof(IsSignedOff));
        OnPropertyChanged(nameof(SignOffBlockReason));
        SignOffCommand.NotifyCanExecuteChanged();
    }
    
    partial void OnActiveToolChanged(ViewerTool value)
    {
        if (value != ViewerTool.Densitometer)
            HoverPercentText = string.Empty;

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
    
    /// <summary>Called by the view when the viewer's zoom changed. Zoom is native-relative:
    /// 1.0 = one plate pixel per screen pixel, regardless of the preview decimation.</summary>
    public void UpdateViewState(double nativeZoom, ViewTransform? view = null)
    {
        View = view;
        ZoomLevel = nativeZoom;
    }
    
    // ---- side-by-side compare ----------------------------------------------------------------

    [ObservableProperty]
    private bool _isCompareMode;

    [ObservableProperty]
    private SeparationViewModel? _compareLeft;

    [ObservableProperty]
    private SeparationViewModel? _compareRight;

    [ObservableProperty]
    private bool _compareRightIsExternal;

    [ObservableProperty]
    private string _compareExternalLabel = string.Empty;

    [ObservableProperty]
    private PlateComparePresentation _comparePresentation = PlateComparePresentation.SideBySide;

    [ObservableProperty]
    private bool _compareSyncLocked = true;

    [ObservableProperty]
    private int _differenceThreshold = 24;

    [ObservableProperty]
    private string _differenceSummary = "—";

    public bool IsDifferenceMode => ComparePresentation == PlateComparePresentation.Difference;

    public bool CanSwapCompare => !CompareRightIsExternal;

    partial void OnComparePresentationChanged(PlateComparePresentation value)
    {
        OnPropertyChanged(nameof(IsDifferenceMode));
        CompareModeChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnCompareSyncLockedChanged(bool value) { }

    /// <summary>Plates loaded from a second file for "compare with job".</summary>
    public List<PlateLayer> CompareExternalLayers { get; } = new();

    /// <summary>Raised whenever the compare panes need to be rebuilt (mode, selection, external load).</summary>
    public event EventHandler? CompareModeChanged;

    public string CompareRightTitle => CompareRightIsExternal && !string.IsNullOrWhiteSpace(CompareExternalLabel)
        ? CompareExternalLabel
        : CompareRight?.Name ?? "No plate";

    partial void OnIsCompareModeChanged(bool value)
    {
        if (value)
        {
            CompareLeft ??= Separations.FirstOrDefault();
            CompareRight ??= Separations.Skip(1).FirstOrDefault() ?? Separations.FirstOrDefault();
        }

        CompareModeChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnCompareLeftChanged(SeparationViewModel? value) =>
        CompareModeChanged?.Invoke(this, EventArgs.Empty);

    partial void OnCompareRightChanged(SeparationViewModel? value)
    {
        // Picking a plate ends "external file" mode; that setter raises the change itself.
        if (CompareRightIsExternal)
            CompareRightIsExternal = false;
        else
            CompareModeChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnCompareRightIsExternalChanged(bool value)
    {
        OnPropertyChanged(nameof(CompareRightTitle));
        OnPropertyChanged(nameof(CanSwapCompare));
        CompareModeChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnCompareExternalLabelChanged(string value) => OnPropertyChanged(nameof(CompareRightTitle));

    [RelayCommand]
    private void ToggleCompare() => IsCompareMode = !IsCompareMode;

    [RelayCommand]
    private void ExitCompare() => IsCompareMode = false;

    [RelayCommand]
    private void SwapComparePlates()
    {
        if (CompareRightIsExternal)
            return;

        (CompareLeft, CompareRight) = (CompareRight, CompareLeft);
    }

    [RelayCommand]
    private void UseJobPlateForCompare() => CompareRightIsExternal = false;

    [RelayCommand]
    private async Task CompareWithFileAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Compare with another job",
            Filter = "RIP output|*.tif;*.tiff;*.pbuf;*.hqn;*.hpb;*.len;*.lenx|All files|*.*"
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            IsLoading = true;
            StatusText = "Loading comparison job...";
            var job = await _parserRegistry.ParseAsync(dialog.FileName);
            var warnings = new List<string>();
            var plates = await _plateLoader.LoadAsync(job, warnings, null, CancellationToken.None);

            CompareExternalLayers.Clear();
            CompareExternalLayers.AddRange(plates);
            CompareExternalLabel = Path.GetFileName(dialog.FileName);

            if (plates.Count == 0)
            {
                StatusText = $"No raster in {CompareExternalLabel}" +
                             (warnings.Count > 0 ? $": {warnings[0]}" : ".");
                return;
            }

            CompareRightIsExternal = true;
            if (!IsCompareMode)
                IsCompareMode = true;

            StatusText = $"Comparing with {CompareExternalLabel} ({plates.Count} plate(s))";
        }
        catch (Exception ex)
        {
            StatusText = $"Compare failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

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

        // The decoded raster is usually smaller than the native plate. Both the pixel size the
        // viewer receives and the mm scale must describe that raster, not the 1:1 plate.
        var baseFactor = _layers.Select(l => l.Pyramid?.Base.Factor ?? 1).DefaultIfEmpty(1).Max();
        PreviewDpi = job.Metadata.Resolution / Math.Max(1, baseFactor);

        var rasterWidth = _layers.Count > 0 ? _layers.Max(l => l.Width) : job.Metadata.Width;
        var rasterHeight = _layers.Count > 0 ? _layers.Max(l => l.Height) : job.Metadata.Height;

        PlatesLoaded?.Invoke(this,
            new PlateSetEventArgs(_layers, rasterWidth, rasterHeight, PixelsPerMm));
    }
    
    /// <summary>Show a job's plate list when no raster could be loaded for it.</summary>
    private void ShowJobWithoutRaster(PrintJob job)
    {
        ApplyJob(job, Array.Empty<PlateLayer>());
        SetPreflight(PreflightAnalyzer.Run(job, Array.Empty<PlateLayer>()));
    }
    
    private void SetPreflight(IReadOnlyList<PreflightResult> results)
    {
        var items = results.Select(r => new PreflightItem(r.Title, r.Detail, r.Status, r.Focus)).ToList();
        PreflightChecks.ReplaceAll(items);

        var issues = items
            .Where(item => item.Status != PreflightStatus.Pass)
            .OrderBy(item => item.Status switch
            {
                PreflightStatus.Fail => 0,
                PreflightStatus.Warning => 1,
                PreflightStatus.NotRun => 2,
                _ => 3
            })
            .ToList();

        PreflightIssues.ReplaceAll(issues);

        var passed = items.Count(item => item.Status == PreflightStatus.Pass);
        HasPreflightPasses = passed > 0;
        PreflightPassedSummary = passed switch
        {
            0 => string.Empty,
            1 => "1 check passed",
            _ => $"{passed} checks passed"
        };
        
        var pass = results.Count(r => r.Status == PreflightStatus.Pass);
        var warn = results.Count(r => r.Status == PreflightStatus.Warning);
        var fail = results.Count(r => r.Status == PreflightStatus.Fail);
        var notRun = results.Count(r => r.Status == PreflightStatus.NotRun);
        PreflightSummary = $"{pass} pass · {warn} warn · {fail} fail · {notRun} not run";
        OnPropertyChanged(nameof(SignOffBlockReason));
        SignOffCommand.NotifyCanExecuteChanged();
    }
    
    // ---- tools -----------------------------------------------------------------------------------
    
    [RelayCommand]
    private void SetTool(ViewerTool tool) => ActiveTool = tool;

    [RelayCommand]
    private void TogglePreflightPane() => ShowPreflightPane = !ShowPreflightPane;
    
    /// <summary>Cursor moved over the page; <paramref name="imagePoint"/> is in plate pixels (null = left the page).</summary>
    public void OnCursorMoved(System.Windows.Point? imagePoint)
    {
        if (imagePoint is not { } p || CurrentJob == null)
        {
            CursorXText = CursorYText = "--";
            HoverPercentText = string.Empty;
            return;
        }
        
        var ppm = PixelsPerMm;
        CursorXText = $"{p.X / ppm:0.00}";
        CursorYText = $"{p.Y / ppm:0.00}";
        
        // Coverage is a densitometer reading: only sample while that tool is selected.
        if (ActiveTool != ViewerTool.Densitometer)
        {
            HoverPercentText = string.Empty;
            return;
        }

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

        HoverPercentText = DensityReadings.Count == 0
            ? string.Empty
            : string.Join("   ", DensityReadings.Select(r => $"{Chip(r.Name)} {r.PercentText}"));
    }

    private static string Chip(string name) => name.ToLowerInvariant() switch
    {
        "cyan" => "C",
        "magenta" => "M",
        "yellow" => "Y",
        "black" => "K",
        "white" => "W",
        "varnish" => "V",
        _ => name.Length > 0 ? name[..1].ToUpperInvariant() : "?"
    };
    
    /// <summary>Rectangle dragged in the viewer; bounds are in plate pixels.</summary>
    public void OnRectangleMeasured(System.Windows.Rect bounds, bool completed)
    {
        if (bounds.Width < 1 || bounds.Height < 1)
            return;

        var ppm = Math.Max(PixelsPerMm, 0.0001);
        var width = bounds.Width / ppm;
        var height = bounds.Height / ppm;
        var area = width * height;
        var perimeter = 2 * (width + height);

        CaliperText = $"{width:0.00} × {height:0.00} mm";
        CaliperDetail = $"Area {area:0.00} mm²   Perimeter {perimeter:0.00} mm";
        HasCaliper = true;
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
    
    /// <summary>Empty when sign-off is allowed; otherwise the reason shown in the button tooltip.</summary>
    public string SignOffBlockReason
    {
        get
        {
            if (CurrentJob is null)
                return "No job open.";
            if (JobStatus == JobStatus.Approved)
                return "This job is already signed off.";
            if (JobStatus == JobStatus.Loading)
                return "The job is still loading.";

            if (PreflightChecks.Count == 0)
                return "Run preflight before signing off.";

            var failed = PreflightChecks.Where(c => c.Status == PreflightStatus.Fail)
                                        .Select(c => c.Title).ToList();
            if (failed.Count > 0)
                return "Preflight failed: " + string.Join(", ", failed);

            var notRun = PreflightChecks.Where(c => c.Status == PreflightStatus.NotRun)
                                        .Select(c => c.Title).ToList();
            if (notRun.Count > 0)
                return "Not run: " + string.Join(", ", notRun);

            return string.Empty;
        }
    }

    private bool CanSignOff() => string.IsNullOrEmpty(SignOffBlockReason);
    
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

        if (PreflightChecks.Count == 0)
        {
            var neverRun = System.Windows.MessageBox.Show(
                "Preflight has not been run for this job." + Environment.NewLine + Environment.NewLine +
                "Sign off without any checks?",
                "Sign-off — no preflight",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning,
                System.Windows.MessageBoxResult.No);

            if (neverRun != System.Windows.MessageBoxResult.Yes)
            {
                StatusText = "Sign-off cancelled: preflight has not been run.";
                return;
            }
        }

        var notRun = PreflightChecks
            .Where(c => c.Status == PreflightStatus.NotRun)
            .Select(c => c.Title)
            .ToList();

        if (notRun.Count > 0)
        {
            var list = string.Join(Environment.NewLine, notRun.Select(title => "• " + title));
            var answer = System.Windows.MessageBox.Show(
                $"These preflight checks have not run:{Environment.NewLine}{Environment.NewLine}{list}" +
                $"{Environment.NewLine}{Environment.NewLine}Sign off without them?",
                "Sign-off — incomplete preflight",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning,
                System.Windows.MessageBoxResult.No);

            if (answer != System.Windows.MessageBoxResult.Yes)
            {
                StatusText = "Sign-off cancelled: preflight has not-run checks.";
                return;
            }
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

/// <summary>How the two selected plates are presented while compare mode is on.</summary>
public enum PlateComparePresentation
{
    SideBySide,
    Slide,
    Blink,
    Difference,
    RipVsReference
}
