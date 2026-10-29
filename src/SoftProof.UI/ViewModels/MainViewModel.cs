using System.Windows;
using System.Windows.Media;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoftProof.Core.Interfaces;
using SoftProof.Core.Analysis;
using SoftProof.Core.Models;
using SoftProof.Core.Services;
using SoftProof.RIPParsers;
using SoftProof.RIPParsers.Raster.Pyramid;
using SoftProof.Viewer;
using SoftProof.Tools;
using SoftProof.UI.Services;

namespace SoftProof.UI.ViewModels;

/// <summary>
/// Main application view model
/// </summary>
public partial class MainViewModel : ObservableObject
{
    // Synthetic IDG / densitometer probes are generated at a bounded size. Using the native
    // plate size here tried to allocate several gigabytes and overflowed y * width.
    private const int SyntheticProbeSize = 1024;
    private readonly RipParserRegistry _parserRegistry;
    private readonly IFileDialogService _fileDialogs;
    private readonly IColorEngine _colorEngine;
    private readonly InverseDotGainTool _idgTool;
    private readonly DensitometerTool _densitometer;
    private readonly ColorMeasurementTool _colorMeasurement;
    private readonly IPlateLoader _plateLoader;
    private readonly PyramidCache _pyramidCache;
    
    [ObservableProperty]
    private PrintJob? _currentJob;
    
    [ObservableProperty]
    private JobStatus _jobStatus = JobStatus.Pending;
    
    [ObservableProperty]
    private string _statusText = "Ready";
    
    [ObservableProperty]
    private double _zoomLevel = 1.0;
    
    [ObservableProperty]
    private bool _softProofingEnabled = false;
    
    [ObservableProperty]
    private SeparationColor _activeSeparation = SeparationColor.ProcessCyan;
    
    [ObservableProperty]
    private bool _showSeparations = true;
    
    [ObservableProperty]
    private bool _showComposite = false;
    
    [ObservableProperty]
    private ProofSettings _proofSettings = new();
    
    [ObservableProperty]
    private MeasurementResult? _lastMeasurement;
    
    [ObservableProperty]
    private StochasticAnalysis? _stochasticAnalysis;
    
    [ObservableProperty]
    private double _loadProgress;

    [ObservableProperty]
    private bool _isLoading;

    private CancellationTokenSource? _loadCts;

    [RelayCommand]
    private void CancelLoad()
    {
        if (!IsLoading)
            return;

        _loadCts?.Cancel();
        StatusText = "Cancelling…";
    }

    [ObservableProperty]
    private string _viewScaleText = "—";

    [ObservableProperty]
    private bool _showOriginalPixels;

    [ObservableProperty]
    private bool _showContentOverlay;

        [ObservableProperty]
    private bool _simulateMetallicPaper;

    [ObservableProperty]
    private bool _useCustomSubstrate;

    [ObservableProperty]
    private Color _substrateColor = Colors.White;

    [ObservableProperty]
    private double _backSideOpacity;

    [ObservableProperty]
    private string _orientationText = "0°";

    [ObservableProperty]
    private int _rotationDegrees;

    [ObservableProperty]
    private bool _isFlipHorizontal;

    [ObservableProperty]
    private bool _isFlipVertical;

    [ObservableProperty]
    private string _viewTransformText = "none";

    /// <summary>Segmented control needs both sides of the substrate choice as bindable booleans.</summary>
    public bool OpaquePaper
    {
        get => !SimulateMetallicPaper && !UseCustomSubstrate;
        set
        {
            if (value)
            {
                SimulateMetallicPaper = false;
                UseCustomSubstrate = false;
            }
        }
    }

    /// <summary>Paper tint fed to the viewer (and native detail) for the current substrate choice.</summary>
    public (float R, float G, float B) SubstrateRgb => UseCustomSubstrate
        ? (SubstrateColor.R / 255f, SubstrateColor.G / 255f, SubstrateColor.B / 255f)
        : SimulateMetallicPaper ? (0.80f, 0.79f, 0.74f) : (1f, 1f, 1f);

    partial void OnSimulateMetallicPaperChanged(bool value)
    {
        if (value)
            UseCustomSubstrate = false;
        OnPropertyChanged(nameof(OpaquePaper));
        OnPropertyChanged(nameof(SubstrateRgb));
    }

    partial void OnUseCustomSubstrateChanged(bool value)
    {
        if (value)
            SimulateMetallicPaper = false;
        OnPropertyChanged(nameof(OpaquePaper));
        OnPropertyChanged(nameof(SubstrateRgb));
    }

    public Brush SubstrateBrush => new SolidColorBrush(SubstrateColor);

    partial void OnSubstrateColorChanged(Color value)
    {
        OnPropertyChanged(nameof(SubstrateRgb));
        OnPropertyChanged(nameof(SubstrateBrush));
    }

    partial void OnRotationDegreesChanged(int value)
    {
        OnPropertyChanged(nameof(IsRotation0));
        OnPropertyChanged(nameof(IsRotation90));
        OnPropertyChanged(nameof(IsRotation180));
        OnPropertyChanged(nameof(IsRotation270));
    }

    public bool IsRotation0 => RotationDegrees == 0;
    public bool IsRotation90 => RotationDegrees == 90;
    public bool IsRotation180 => RotationDegrees == 180;
    public bool IsRotation270 => RotationDegrees == 270;

    /// <summary>Orientation commands are executed by the window, which owns the viewer.</summary>
    public event EventHandler<string>? ViewCommandRequested;

    [RelayCommand]
    private void RotateView() => ViewCommandRequested?.Invoke(this, "rotate");

    [RelayCommand]
    private void FlipHorizontal() => ViewCommandRequested?.Invoke(this, "fliph");

    [RelayCommand]
    private void FlipVertical() => ViewCommandRequested?.Invoke(this, "flipv");

    [RelayCommand]
    private void ResetOrientation() => ViewCommandRequested?.Invoke(this, "reset");

    [RelayCommand]
    private void RotateTo(string degrees) => ViewCommandRequested?.Invoke(this, "rotate:" + degrees);

    /// <summary>Status text with both numbers: native plate resolution and current view scale.</summary>
    public void UpdateViewScale(double nativeZoom, int factor, int level = 0, bool nativePixels = false)
    {
        IsDotsView = nativeZoom >= 15.0;

        // At 1:1 the view IS native, even for the frames before the detail tiles land.
        // Without this the status line briefly reads "view 300 dpi · 100% · Overview 1/8".
        if (nativeZoom >= 0.999)
            nativePixels = true;

        if (nativeZoom <= 0)
        {
            ViewScaleText = "—";
            return;
        }

        var plateDpi = CurrentJob?.Metadata.Resolution ?? 0;
        var percent = nativeZoom * 100;
        var levelFactor = (long)Math.Max(1, factor) << Math.Max(0, level);

        // Native pixels are 1:1; anything coarser is an overview at 1/levelFactor of the plate.
        var viewDpi = nativePixels ? plateDpi : levelFactor > 1 ? plateDpi / levelFactor : plateDpi;
        var overview = nativePixels
            ? "Native pixels"
            : levelFactor > 1
                ? $"Overview 1/{levelFactor}"
                : string.Empty;

        var percentText = View?.ZoomText ?? $"{percent:0}%";
        var scale = overview.Length > 0
            ? $"{percentText} · {overview}"
            : percentText;

        ViewScaleText = plateDpi > 0
            ? $"{plateDpi:0} dpi plate · view {viewDpi:0} dpi · {scale}"
            : scale;
    }

    /// <summary>Overlay compare modes report their own zoom; keep the readout truthful.</summary>
    public void UpdateOverlayZoom(double nativeZoom, ViewTransform? view = null)
    {
        View = view;
        ZoomLevel = nativeZoom;

        var plateDpi = CurrentJob?.Metadata.Resolution ?? 0;
        var percentText = view?.ZoomText ?? $"{nativeZoom * 100:0}%";
        ViewScaleText = plateDpi > 0
            ? $"{plateDpi:0} dpi plate · view {plateDpi:0} dpi · {percentText}"
            : percentText;
    }

    [ObservableProperty]
    private bool _isDotsView;

    [ObservableProperty]
    private double _previewDpi;

    [ObservableProperty]
    private bool _showPreflightPane = true;

    [ObservableProperty]
    private bool _showSimulationSection;

    [ObservableProperty]
    private bool _showNavigatorSection = true;

    [ObservableProperty]
    private bool _showQueueSection;

    [ObservableProperty]
    private string _navigatorViewportText = "—";

    partial void OnShowPreflightPaneChanged(bool value) => SaveUiState();
    partial void OnShowSimulationSectionChanged(bool value) => SaveUiState();
    partial void OnShowNavigatorSectionChanged(bool value) => SaveUiState();
    partial void OnShowQueueSectionChanged(bool value) => SaveUiState();

    private static string UiStatePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoftProof", "ui.json");

    private void LoadUiState()
    {
        try
        {
            var path = UiStatePath();
            if (!File.Exists(path))
                return;

            var state = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(path));
            if (state is null)
                return;

            if (state.TryGetValue(nameof(ShowPreflightPane), out var preflightPane))
                ShowPreflightPane = preflightPane;
            if (state.TryGetValue(nameof(ShowSimulationSection), out var simulation))
                ShowSimulationSection = simulation;
            if (state.TryGetValue(nameof(ShowNavigatorSection), out var navigator))
                ShowNavigatorSection = navigator;
            if (state.TryGetValue(nameof(ShowQueueSection), out var queue))
                ShowQueueSection = queue;
        }
        catch
        {
            // Layout preferences must never block startup.
        }
    }

    private void SaveUiState()
    {
        try
        {
            var path = UiStatePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var state = new Dictionary<string, bool>
            {
                [nameof(ShowPreflightPane)] = ShowPreflightPane,
                [nameof(ShowSimulationSection)] = ShowSimulationSection,
                [nameof(ShowNavigatorSection)] = ShowNavigatorSection,
                [nameof(ShowQueueSection)] = ShowQueueSection
            };
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(state));
        }
        catch
        {
            // Ignore: the sections simply keep their defaults next time.
        }
    }

    [ObservableProperty]
    private bool _autoRefresh = true;

    /// <summary>Open the newest finished file from a watched folder automatically.</summary>
    [ObservableProperty]
    private bool _autoOpenNewFiles = true;

    /// <summary>Raised when the watched folder set changes (job opened, folder added).</summary>
    public event EventHandler<IReadOnlyList<string>>? WatchFoldersChanged;

    public IReadOnlyList<string> WatchFolders =>
        RipQueueService.FoldersFromEnvironment(CurrentJob?.FilePath)
            .Concat(_extraQueueFolders)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public ObservableCollectionEx<RipQueueEntry> RipQueue { get; } = new();

    private readonly List<string> _extraQueueFolders = new();

    [RelayCommand]
    private void AddWatchFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Add RIP output folder" };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName))
            return;

        if (!_extraQueueFolders.Contains(dialog.FolderName, StringComparer.OrdinalIgnoreCase))
            _extraQueueFolders.Add(dialog.FolderName);

        RefreshQueue();
    }

    [ObservableProperty]
    private string _queueFolders = string.Empty;

    [RelayCommand]
    private void RefreshQueue()
    {
        var folders = RipQueueService.FoldersFromEnvironment(CurrentJob?.FilePath)
                                         .Concat(_extraQueueFolders)
                                         .Distinct(StringComparer.OrdinalIgnoreCase)
                                         .ToArray();
        var service = new RipQueueService(folders);
        RipQueue.ReplaceAll(service.Scan());
        QueueFolders = string.Join("  ·  ", service.Folders);
        WatchFoldersChanged?.Invoke(this, folders);

        var loadedPath = CurrentJob?.FilePath;
        foreach (var entry in RipQueue)
            entry.IsLoaded = !string.IsNullOrEmpty(loadedPath) &&
                             string.Equals(entry.Path, loadedPath, StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private async Task OpenQueueEntryAsync(RipQueueEntry? entry)
    {
        if (entry is null)
            return;

        await LoadJobsAsync(new[] { entry.Path });
    }

    public ObservableCollectionEx<SeparationViewModel> Separations { get; } = new();
    public ObservableCollectionEx<RecentFileEntry> RecentFiles { get; } = new();
    
    public MainViewModel(
        RipParserRegistry parserRegistry,
        IFileDialogService fileDialogs,
        IColorEngine colorEngine,
        InverseDotGainTool idgTool,
        DensitometerTool densitometer,
        ColorMeasurementTool colorMeasurement,
        IPlateLoader plateLoader,
        PyramidCache pyramidCache)
    {
        _parserRegistry = parserRegistry;
        _fileDialogs = fileDialogs;
        _colorEngine = colorEngine;
        _idgTool = idgTool;
        _densitometer = densitometer;
        _colorMeasurement = colorMeasurement;
        _plateLoader = plateLoader;
        _pyramidCache = pyramidCache;
        
        LoadRecentFiles();
        LoadUiState();
        LoadPressProfiles();
        RefreshQueue();
    }
    
    [RelayCommand]
    private async Task OpenFileAsync()
    {
        if (JobStatus == JobStatus.Loading)
            return; // a job is already being loaded

        // Build the filter from whatever the registered parsers actually support,
        // so adding a parser automatically adds its extensions to the dialog.
        var patterns = string.Join(";", _parserRegistry.SupportedExtensions.Select(ext => "*" + ext));
        var filter = $"RIP jobs|{patterns}|All files|*.*";

        var files = _fileDialogs.ShowOpenFilesDialog(
            "Open RIP Job",
            filter,
            GetInitialDirectory(),
            multiselect: true); // separations are often one file per plate

        if (files is null || files.Count == 0)
            return; // cancelled

        await LoadJobsAsync(files);
    }
    
    private string? GetInitialDirectory()
    {
        var last = RecentFiles.FirstOrDefault();
        return last is null ? null : Path.GetDirectoryName(last.Path);
    }
    
    [RelayCommand]
    private async Task OpenRecentFileAsync(string filePath)
    {
        await LoadJobAsync(filePath);
    }
    
    [RelayCommand]
    public Task LoadJobAsync(string filePath) => LoadJobsAsync(new[] { filePath });
    
    /// <summary>
    /// Loads one file, or several files that together form one job (e.g. one TIFF per separation).
    /// </summary>
    public async Task LoadJobsAsync(IReadOnlyList<string> filePaths)
    {
        if (filePaths.Count == 0)
            return;
        
        var displayName = filePaths.Count == 1
            ? Path.GetFileName(filePaths[0])
            : $"{filePaths.Count} files";
        
        var warnings = new List<string>();

        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;
        IsLoading = true;
        LoadProgress = 0;

        var progress = new Progress<PlateLoadProgress>(p =>
        {
            LoadProgress = p.Percent;
            StatusText = $"{p.Stage}: {p.PlateName} ({p.Percent:0}%)";
        });

        try
        {
            JobStatus = JobStatus.Loading;
            StatusText = $"Loading {displayName}...";

            var job = filePaths.Count == 1
                ? await _parserRegistry.ParseAsync(filePaths[0])
                : await _parserRegistry.ParseMultipleAsync(filePaths);
            if (job.Separations.Count > 0)
                ActiveSeparation = job.Separations[0].Color;

            // Decode the actual plate rasters (TIFF strips / page-buffer payloads).
            StatusText = $"Rasterising {job.Separations.Count} plate(s)...";
            var plates = await _plateLoader.LoadAsync(job, warnings, progress, token);

            if (plates.Count > 0)
            {
                ApplyJob(job, plates);
                SetPreflight(PreflightAnalyzer.Run(job, plates));
                JobStatus = JobStatus.Ready;
                StatusText =
                    $"Loaded: {job.Name} - {plates.Count} of {job.Separations.Count} plates, " +
                    $"{job.Metadata.Width}x{job.Metadata.Height} px @ {job.Metadata.Resolution} dpi" +
                    (warnings.Count > 0 ? $" ({warnings.Count} warning(s): {warnings[0]})" : string.Empty);
            }
            else
            {
                // Metadata parsed but nothing decodable: keep the plate list visible and say why.
                ShowJobWithoutRaster(job);
                JobStatus = JobStatus.Ready;
                StatusText = warnings.Count > 0
                    ? $"Loaded: {job.Name} - no raster: {warnings[0]}"
                    : $"Loaded: {job.Name} ({job.Separations.Count} separations) - no raster data in this file.";
            }

            AddToRecentFiles(filePaths[0]);
            RefreshQueue();
        }
        catch (OperationCanceledException)
        {
            JobStatus = JobStatus.Pending;
            StatusText = $"Loading cancelled: {displayName}";
        }
        catch (Exception ex)
        {
            JobStatus = JobStatus.Error;
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
            LoadProgress = 0;
        }
    }
    
    private async Task LoadSeparationAsync(Separation separation)
    {
        ActiveSeparation = separation.Color;
        await LoadSeparationDataAsync(separation);
    }
    
    private async Task LoadSeparationDataAsync(Separation separation)
    {
        // In real app: load actual separation data from file
        // For now, create test pattern
        StatusText = $"Loading separation: {separation.Name}...";
        
        // Simulate loading
        await Task.Delay(100);
        
        // Notify viewer to load (via event or messenger)
        SeparationLoaded?.Invoke(this, new SeparationLoadEventArgs(separation));
    }
    
    /// <summary>Delete the preview-pyramid cache; it is safe to rebuild and can grow to 512 MB.</summary>
    [RelayCommand]
    private void ClearPyramidCache()
    {
        var root = _pyramidCache.RootDirectory;
        if (!Directory.Exists(root))
        {
            StatusText = "Pyramid cache is already empty.";
            return;
        }

        long bytes = 0;
        var files = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try
            {
                bytes += new FileInfo(file).Length;
                File.Delete(file);
                files++;
            }
            catch (IOException)
            {
                // A cache file in use is skipped; it will be reused next time.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        StatusText = files == 0 && bytes == 0
            ? "Pyramid cache is already empty."
            : $"Cleared pyramid cache: {files} file(s), {bytes / (1024.0 * 1024.0):0.0} MB.";
    }

    [RelayCommand]
    private void ToggleSoftProofing()
    {
        SoftProofingEnabled = !SoftProofingEnabled;
        StatusText = SoftProofingEnabled ? "Soft proofing ON" : "Soft proofing OFF";
        SoftProofingToggled?.Invoke(this, SoftProofingEnabled);
    }
    
    [RelayCommand]
    private async Task RunIdgAnalysisAsync()
    {
        if (CurrentJob == null) return;
        
        StatusText = "Running IDG analysis...";
        
        try
        {
            // Get active separation data
            var sep = CurrentJob.Separations.FirstOrDefault(s => s.Color == ActiveSeparation);
            if (sep == null) return;
            
            // Simulate separation data
            var testData = GenerateTestSeparationData(SyntheticProbeSize, SyntheticProbeSize);
            
            var parameters = new IdgParameters
            {
                Resolution = CurrentJob.Metadata.Resolution,
                ScreenFrequency = CurrentJob.ProofSettings.ScreenFrequency,
                ScreeningType = CurrentJob.ProofSettings.ScreeningType,
                ScreenAngle = 45,
                OpticalDotGainMm = 0.03f,
                PaperScatterFactor = 1.0f
            };
            
            // Apply IDG
            var result = _idgTool.ApplyIdgSimulation(testData, SyntheticProbeSize, SyntheticProbeSize, parameters);
            
            // Analyze quality
            StochasticAnalysis = _idgTool.AnalyzeStochasticQuality(testData, SyntheticProbeSize, SyntheticProbeSize);
            
            StatusText = $"IDG Analysis complete. Quality: {StochasticAnalysis.QualityScore:F1}/100";
            IdgAnalysisCompleted?.Invoke(this, StochasticAnalysis);
        }
        catch (Exception ex)
        {
            StatusText = $"IDG Analysis failed: {ex.Message}";
        }
    }
    
    [RelayCommand]
    private void MeasureDensity()
    {
        if (CurrentJob == null) return;
        
        // Get measurement region from viewer (center 10x10mm)
        var region = new MeasurementRegion
        {
            X = SyntheticProbeSize / 2 - 500,
            Y = SyntheticProbeSize / 2 - 500,
            Width = 1000,
            Height = 1000,
            Name = "Center"
        };
        
        var sep = CurrentJob.Separations.FirstOrDefault(s => s.Color == ActiveSeparation);
        if (sep == null) return;
        
        var testData = GenerateTestSeparationData(SyntheticProbeSize, SyntheticProbeSize);
        LastMeasurement = _densitometer.MeasureDensity(testData, SyntheticProbeSize, SyntheticProbeSize, region);
        
        StatusText = $"Density: {LastMeasurement.Density:F3}";
    }
    
    [RelayCommand]
    private void FitToWindow()
    {
        FitToWindowRequested?.Invoke(this, EventArgs.Empty);
    }
    
    [RelayCommand]
    private void ZoomIn() => RequestZoom(ZoomLevel * 1.25);
    
    [RelayCommand]
    private void ZoomOut() => RequestZoom(ZoomLevel / 1.25);
    
    [RelayCommand]
    private void ZoomActualSize() => RequestZoom(1.0);
    
    [RelayCommand]
    private void ZoomToDots() => RequestZoom(16.0);
    
    [RelayCommand]
    private void ShowProofSettings()
    {
        ProofSettingsDialogRequested?.Invoke(this, ProofSettings);
    }
    
    private byte[] GenerateTestSeparationData(int width, int height)
    {
        // Generate stochastic screening test pattern
        var data = new byte[((long)width * height + 7) / 8];
        var random = new Random(42); // Deterministic
        
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var idx = y * width + x;
                var byteIdx = idx / 8;
                var bitPos = 7 - (idx % 8);
                
                // Stochastic threshold
                var threshold = (float)random.NextDouble();
                var tint = 0.5f; // 50% tint
                
                if (threshold < tint)
                    data[byteIdx] |= (byte)(1 << bitPos);
            }
        }
        
        return data;
    }
    
    private static string RecentStorePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoftProof", "recent.json");

    private sealed class RecentFileDto
    {
        public string Path { get; set; } = string.Empty;
        public bool Pinned { get; set; }
    }

    private void LoadRecentFiles()
    {
        var path = RecentStorePath();
        if (!File.Exists(path))
            return;

        try
        {
            var json = File.ReadAllText(path);
            var entries = new List<RecentFileEntry>();

            try
            {
                var dtos = System.Text.Json.JsonSerializer.Deserialize<List<RecentFileDto>>(json);
                if (dtos is not null)
                    entries.AddRange(dtos.Where(d => !string.IsNullOrWhiteSpace(d.Path))
                                         .Select(d => new RecentFileEntry(d.Path, d.Pinned)));
            }
            catch (System.Text.Json.JsonException)
            {
                // Older builds stored a plain string[]; migrate it silently.
                var old = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? new();
                entries.AddRange(old.Select(f => new RecentFileEntry(f)));
            }

            foreach (var entry in entries.Take(10))
                RecentFiles.Add(entry);
        }
        catch
        {
            // A corrupt recent list must never block startup.
        }
    }

    private void SaveRecentFiles()
    {
        try
        {
            var path = RecentStorePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var payload = RecentFiles.Select(e => new RecentFileDto { Path = e.Path, Pinned = e.Pinned }).ToList();
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(payload));
        }
        catch
        {
            // Preferences are a convenience; losing them is not an error worth surfacing.
        }
    }

    private void AddToRecentFiles(string filePath)
    {
        var existing = RecentFiles.FirstOrDefault(e =>
            string.Equals(e.Path, filePath, StringComparison.OrdinalIgnoreCase));
        var pinned = existing?.Pinned ?? false;

        if (existing is not null)
            RecentFiles.Remove(existing);

        RecentFiles.Insert(0, new RecentFileEntry(filePath, pinned));

        // Trim the oldest unpinned entries; a pinned file stays until the user unpins it.
        while (RecentFiles.Count > 10)
        {
            var victim = RecentFiles.LastOrDefault(e => !e.Pinned);
            if (victim is null)
                break;
            RecentFiles.Remove(victim);
        }

        SaveRecentFiles();
    }

    [RelayCommand]
    private void TogglePinRecent(RecentFileEntry? entry)
    {
        if (entry is null)
            return;

        entry.Pinned = !entry.Pinned;
        RecentFiles.ReplaceAll(RecentFiles.OrderByDescending(e => e.Pinned));
        SaveRecentFiles();
    }

    [RelayCommand]
    private void ClearRecent()
    {
        RecentFiles.Clear();
        SaveRecentFiles();
    }

    // ---- File menu: folders, reload, close, report, preferences --------------------------------

    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Open RIP job folder" };
        if (dialog.ShowDialog() != true)
            return;

        var folder = dialog.FolderName;
        var extensions = new[] { ".tif", ".tiff", ".pbuf", ".hqn", ".hpb", ".len", ".lenx" };

        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder)
                             .Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                             .OrderByDescending(File.GetLastWriteTimeUtc)
                             .Take(8)
                             .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Cannot read {folder}: {ex.Message}";
            return;
        }

        if (files.Count == 0)
        {
            StatusText = $"No RIP output (.tif, .pbuf, .len) found in {folder}.";
            return;
        }

        await LoadJobsAsync(files);
    }

    /// <summary>File menu alias for the queue's Add watch folder action.</summary>
    [RelayCommand]
    private void WatchFolder() => AddWatchFolder();

    [RelayCommand]
    private async Task ReloadJobAsync()
    {
        var path = CurrentJob?.FilePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            StatusText = "Nothing to reload: no job file is open.";
            return;
        }

        await LoadJobAsync(path);
    }

    [RelayCommand]
    private void CloseJob()
    {
        if (CurrentJob is null)
            return;

        CurrentJob = null;
        _layers.Clear();
        Separations.Clear();
        PreflightChecks.Clear();
        HasRaster = false;
        LoadProgress = 0;
        JobStatus = SoftProof.Core.Models.JobStatus.Pending;
        StatusText = "No job open. Drop a TIFF or job folder here, or press Ctrl+O.";

        JobClosed?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(HasJob));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(PressText));
        OnPropertyChanged(nameof(ResolutionText));
    }

    [RelayCommand]
    private void ExportReport()
    {
        if (CurrentJob is null)
        {
            StatusText = "Open a job before exporting a report.";
            return;
        }

        var job = CurrentJob;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export proof report",
            Filter = "HTML report (*.html)|*.html|Text file (*.txt)|*.txt",
            FileName = SanitizeFileName(job.Name) + "-proof-report.html"
        };

        if (dialog.ShowDialog() != true)
            return;

        var html = dialog.FileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
            ? BuildTextReport(job)
            : BuildHtmlReport(job);

        try
        {
            File.WriteAllText(dialog.FileName, html);
            StatusText = $"Report written to {dialog.FileName}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Could not write report: {ex.Message}";
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(clean) ? "job" : clean;
    }

    private string BuildHtmlReport(PrintJob job)
    {
        var encode = new Func<string, string>(System.Net.WebUtility.HtmlEncode);
        var ppm = job.Metadata.Resolution > 0 ? job.Metadata.Resolution / 25.4 : 0;
        var sb = new StringBuilder();

        sb.AppendLine("<!DOCTYPE html><html><head><meta charset='utf-8'>");
        sb.AppendLine("<title>SoftProof report</title>");
        sb.AppendLine("<style>body{font-family:Segoe UI,sans-serif;margin:28px;color:#1a1f2b}" +
                      "h1{font-size:22px;margin:0 0 4px}h2{font-size:15px;margin:22px 0 6px}" +
                      "table{border-collapse:collapse;width:100%;margin:8px 0 4px}" +
                      "th,td{border:1px solid #d7dbe3;padding:6px 8px;font-size:12px;text-align:left;vertical-align:top}" +
                      "th{background:#f2f4f8}.pass{color:#1b7f3a;font-weight:600}.warn{color:#9a6b00;font-weight:600}" +
                      ".fail{color:#b00020;font-weight:600}.muted{color:#6b7280;font-size:11px}</style></head><body>");

        sb.AppendLine($"<h1>{encode(job.Name)}</h1>");
        sb.AppendLine($"<div class='muted'>{encode(job.FilePath)}</div>");
        sb.AppendLine("<table>");
        sb.AppendLine($"<tr><th>Format</th><td>{job.Format}</td><th>Resolution</th><td>{job.Metadata.Resolution} dpi</td></tr>");
        sb.AppendLine($"<tr><th>Separations</th><td>{job.Separations.Count}</td><th>Raster plates</th><td>{_layers.Count}</td></tr>");
        sb.AppendLine($"<tr><th>Press</th><td>{encode(PressText)}</td><th>Exported</th><td>{DateTime.Now:yyyy-MM-dd HH:mm}</td></tr>");
        sb.AppendLine("</table>");

        sb.AppendLine("<h2>Plates</h2><table><tr><th>Separation</th><th>Size (px)</th><th>Size (mm)</th>" +
                      "<th>Ink coverage</th><th>Inked pixels</th><th>Ink bounds (px)</th></tr>");
        foreach (var layer in _layers)
        {
            var mmW = ppm > 0 ? layer.Width / ppm : 0;
            var mmH = ppm > 0 ? layer.Height / ppm : 0;
            var bounds = layer.InkBounds.IsEmpty
                ? "-"
                : $"{layer.InkBounds.X},{layer.InkBounds.Y} {layer.InkBounds.Width}x{layer.InkBounds.Height}";

            sb.AppendLine($"<tr><td>{encode(layer.Name)}</td><td>{layer.Width}x{layer.Height}</td>" +
                          $"<td>{mmW:0.0}x{mmH:0.0}</td><td>{layer.InkedAreaPercent:0.00}%</td>" +
                          $"<td>{layer.InkedPixelPercent:0.000}%</td><td>{bounds}</td></tr>");
        }
        sb.AppendLine("</table>");

        sb.AppendLine("<h2>Preflight</h2><table><tr><th>Check</th><th>Status</th><th>Detail</th></tr>");
        foreach (var check in PreflightChecks)
        {
            var css = check.Status switch
            {
                PreflightStatus.Pass => "pass",
                PreflightStatus.Warning => "warn",
                PreflightStatus.Fail => "fail",
                _ => "muted"
            };

            sb.AppendLine($"<tr><td>{encode(check.Title)}</td><td class='{css}'>{check.Status}</td>" +
                          $"<td>{encode(check.Detail)}</td></tr>");
        }
        sb.AppendLine("</table></body></html>");
        return sb.ToString();
    }

    private string BuildTextReport(PrintJob job)
    {
        var ppm = job.Metadata.Resolution > 0 ? job.Metadata.Resolution / 25.4 : 0;
        var sb = new StringBuilder();
        sb.AppendLine($"SoftProof report — {job.Name}");
        sb.AppendLine(job.FilePath);
        sb.AppendLine($"Format: {job.Format}   Resolution: {job.Metadata.Resolution} dpi   Press: {PressText}");
        sb.AppendLine($"Exported: {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        sb.AppendLine("PLATES");
        foreach (var layer in _layers)
        {
            var mmW = ppm > 0 ? layer.Width / ppm : 0;
            var mmH = ppm > 0 ? layer.Height / ppm : 0;
            sb.AppendLine($"  {layer.Name,-12} {layer.Width}x{layer.Height} px  {mmW:0.0}x{mmH:0.0} mm  " +
                          $"ink {layer.InkedAreaPercent:0.00}%  inked {layer.InkedPixelPercent:0.000}%");
        }
        sb.AppendLine();
        sb.AppendLine("PREFLIGHT");
        foreach (var check in PreflightChecks)
            sb.AppendLine($"  [{check.Status}] {check.Title}: {check.Detail}");
        return sb.ToString();
    }

    public event EventHandler? JobClosed;
    public event EventHandler<ProofSettings>? PreferencesRequested;

    [RelayCommand]
    private void OpenPreferences() => PreferencesRequested?.Invoke(this, ProofSettings);

    // Events for UI communication
    public event EventHandler<SeparationLoadEventArgs>? SeparationLoaded;
    public event EventHandler<bool>? SoftProofingToggled;
    public event EventHandler<StochasticAnalysis>? IdgAnalysisCompleted;
    public event EventHandler? FitToWindowRequested;
    public event EventHandler<double>? ZoomChanged;
    public event EventHandler<ProofSettings>? ProofSettingsDialogRequested;
}

/// <summary>
/// Separation view model for list display
/// </summary>
public partial class SeparationViewModel : ObservableObject
{
    public SeparationViewModel(Separation separation)
    {
        Separation = separation;
        Name = separation.Name;
        Color = separation.Color;
        IsSpotColor = separation.IsSpotColor;
    }
    
    public Separation Separation { get; }

    /// <summary>Source file name, middle-truncated so the leading job name and trailing
    /// separation suffix both stay visible in the inked-area row.</summary>
    public string FileName
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(Separation.FilePath)
                ? Name
                : Path.GetFileName(Separation.FilePath);

            const int max = 40;
            const int head = 18;
            const int tail = 18;
            return name.Length <= max ? name : name[..head] + "…" + name[^tail..];
        }
    }

    [ObservableProperty]
    private string _name;
    
    [ObservableProperty]
    private SeparationColor _color;
    
    [ObservableProperty]
    private bool _isSpotColor;
    
    [ObservableProperty]
    private bool _isVisible = true;
    
    [ObservableProperty]
    private bool _isSelected;
    
    public Color DisplayColor => Color switch
    {
        SeparationColor.ProcessCyan => Colors.Cyan,
        SeparationColor.ProcessMagenta => Colors.Magenta,
        SeparationColor.ProcessYellow => Colors.Yellow,
        SeparationColor.ProcessBlack => Colors.Black,
        SeparationColor.Orange => Colors.Orange,
        SeparationColor.Green => Colors.Green,
        SeparationColor.Violet => Colors.Violet,
        SeparationColor.Red => Colors.Red,
        SeparationColor.Blue => Colors.Blue,
        SeparationColor.White => Colors.White,
        SeparationColor.Varnish => Colors.LightGray,
        SeparationColor.Primer => Colors.Gray,
        _ => Colors.HotPink
    };
}

/// <summary>
/// Event args for separation loading
/// </summary>
public class SeparationLoadEventArgs : EventArgs
{
    public Separation Separation { get; }
    
    public SeparationLoadEventArgs(Separation separation)
    {
        Separation = separation;
    }
}