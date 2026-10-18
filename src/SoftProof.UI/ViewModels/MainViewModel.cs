using System.Windows;
using System.Windows.Media;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoftProof.Core.Interfaces;
using SoftProof.Core.Analysis;
using SoftProof.Core.Models;
using SoftProof.RIPParsers;
using SoftProof.Viewer;
using SoftProof.Tools;
using SoftProof.UI.Services;

namespace SoftProof.UI.ViewModels;

/// <summary>
/// Main application view model
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly RipParserRegistry _parserRegistry;
    private readonly IFileDialogService _fileDialogs;
    private readonly IColorEngine _colorEngine;
    private readonly InverseDotGainTool _idgTool;
    private readonly DensitometerTool _densitometer;
    private readonly ColorMeasurementTool _colorMeasurement;
    private readonly IPlateLoader _plateLoader;
    
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

    public ObservableCollectionEx<SeparationViewModel> Separations { get; } = new();
    public ObservableCollectionEx<string> RecentFiles { get; } = new();
    
    public MainViewModel(
        RipParserRegistry parserRegistry,
        IFileDialogService fileDialogs,
        IColorEngine colorEngine,
        InverseDotGainTool idgTool,
        DensitometerTool densitometer,
        ColorMeasurementTool colorMeasurement,
        IPlateLoader plateLoader)
    {
        _parserRegistry = parserRegistry;
        _fileDialogs = fileDialogs;
        _colorEngine = colorEngine;
        _idgTool = idgTool;
        _densitometer = densitometer;
        _colorMeasurement = colorMeasurement;
        _plateLoader = plateLoader;
        
        LoadRecentFiles();
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
        return string.IsNullOrEmpty(last) ? null : Path.GetDirectoryName(last);
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
            var testData = GenerateTestSeparationData(CurrentJob.Metadata.Width, CurrentJob.Metadata.Height);
            
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
            var result = _idgTool.ApplyIdgSimulation(testData, CurrentJob.Metadata.Width, CurrentJob.Metadata.Height, parameters);
            
            // Analyze quality
            StochasticAnalysis = _idgTool.AnalyzeStochasticQuality(testData, CurrentJob.Metadata.Width, CurrentJob.Metadata.Height);
            
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
            X = CurrentJob.Metadata.Width / 2 - 500,
            Y = CurrentJob.Metadata.Height / 2 - 500,
            Width = 1000,
            Height = 1000,
            Name = "Center"
        };
        
        var sep = CurrentJob.Separations.FirstOrDefault(s => s.Color == ActiveSeparation);
        if (sep == null) return;
        
        var testData = GenerateTestSeparationData(CurrentJob.Metadata.Width, CurrentJob.Metadata.Height);
        LastMeasurement = _densitometer.MeasureDensity(testData, CurrentJob.Metadata.Width, CurrentJob.Metadata.Height, region);
        
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
        var data = new byte[(width * height + 7) / 8];
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
    
    private void LoadRecentFiles()
        {
            // Load from simple JSON file
            var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoftProof", "recent.json");
            if (File.Exists(settingsPath))
            {
                try
                {
                    var json = File.ReadAllText(settingsPath);
                    var files = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? new();
                    foreach (var f in files.Take(10))
                        RecentFiles.Add(f);
                }
                catch { }
            }
        }

        private void AddToRecentFiles(string filePath)
        {
            RecentFiles.Remove(filePath);
            RecentFiles.Insert(0, filePath);
            while (RecentFiles.Count > 10) RecentFiles.RemoveAt(RecentFiles.Count - 1);

            var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoftProof", "recent.json");
            var dir = Path.GetDirectoryName(settingsPath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var json = System.Text.Json.JsonSerializer.Serialize(RecentFiles.ToArray());
            File.WriteAllText(settingsPath, json);
        }
    
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