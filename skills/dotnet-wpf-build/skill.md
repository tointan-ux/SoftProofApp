# dotnet-wpf-build: .NET 8 WPF Application Build Process

## Overview
Build pipeline for SoftProof - a professional prepress soft proofing application inspired by Hamillroad FirstPROOF.

## Prerequisites
- .NET 8 SDK (8.0.426 or later)
- Visual Studio 2022 or VS Code with C# Dev Kit
- Windows 10/11 (x64)

## Build Commands
```powershell
# Restore packages
 dotnet restore SoftProof.sln

# Build Release
 dotnet build SoftProof.sln -c Release --no-restore

# Run the app
 dotnet run --project src/SoftProof.UI/SoftProof.UI.csproj -c Release
```

## Common Issues & Fixes

### StringFormat Escaping in XAML
- XAML StringFormat bindings need double braces: `{0}` becomes `{{0}}`
- Problem: `{Binding Path, StringFormat='{0:F1}'}` causes parse errors
- Fix: Use `{{0:F1}}` (double braces for literal braces in XAML)
- Affected files: MainWindow.xaml, App.xaml, Converters/*.cs

### Project Configuration
- MSB4121 warning: Project configuration not specified for Release|Any CPU
- Fix: Ensure all .csproj files target `net8.0-windows` and have consistent Configuration settings
- May need `<OutputType>WinExe</OutputType>` for UI projects

### Dependencies
- SixLabors.ImageSharp vulnerabilities (NU1903)
- Update to latest versions or pin specific versions
- `dotnet tool update --global dotnet-nuke` for related packages

### App.xaml.cs DI Setup
- Ensure `CreateHostBuilder()` properly registers all services
- `AppHost` property added for ViewModelLocator access
- Services: IRipParser, IColorEngine, InverseDotGainTool, DensitometerTool, ColorMeasurementTool
- SeparationParser, MultiChannelTiffParser, LenFileParser

### ViewModel Fixes
- MainViewModel requires `System.IO` using directive for `Path.GetFileName()`
- MainViewModel requires `System.Windows.Media` using directive
- MainViewModel requires `SoftProof.Core.Interfaces` and `SoftProof.Core.Models`
- MainViewModel requires `SoftProof.RIPParsers` and `SoftProof.Viewer` and `SoftProof.Tools`
- Fix `Properties.Settings.Default` → use JSON file in `%APPDATA%\\SoftProof\\recent.json`

### App.xaml.cs Changes
- Add `internal IHost? AppHost => _host;` property
- Update ViewModelLocator to use `AppHost?.Services`
- OnStartup: `_host = CreateHostBuilder().Build();` then `var mainWindow = _host.Services.GetRequiredService<MainWindow>();`
- Fix AccessViolation when accessing `_host` from ViewModelLocator

### MainWindow.xaml Changes
- `<Window x:Class=\"SoftProof.UI.Views.MainWindow\" ... WindowStyle=\"None\" AllowsTransparency=\"True\" Background=\"Transparent\"`
- Add `WindowsTitleBar` user control
- Add `MenuBar`, `QuickToolbar` user controls
- Add `ProofCanvas`, `SeparationsPanel`, `InspectionToolsPanel` user controls
- Add `DensitometerPanel` at bottom
- Status bar with job info, resolution, zoom, separator selection
- Toast notification system
- Modal host at root level

### SoftProof.UI.csproj Changes
- `<OutputType>WinExe</OutputType>` for executable
- Remove `<ApplicationIcon>Resources\\icon.ico</ApplicationIcon>` if not using icon
- `<UseWPF>true</UseWPF>` required
- MaterialDesignThemes NuGet packages

### SoftProof.RIPParsers.csproj Changes
- Add `Microsoft.Extensions.Logging.Abstractions` package
- Add `SixLabors.ImageSharp` package
- Add `Microsoft.Extensions.Logging` package
- Fix `CanParseAsync` implementations for Harlequin, TIFF, LEN parsers

### SoftProof.ColorManagement Changes
- Add `Microsoft.Extensions.Logging.Abstractions` package
- Add `System.Drawing.Common` package
- LittleCMS lcms2.dll in `runtimes/win-x64/native/`
- Fix `LittleCmsEngine.cs`: `BYTES_SH`, `CHANNELS_SH`, `FORMATTER_FLAGS_SH` as static methods not const expressions
- Fix `IccProfile.Handle` and `ColorTransform.Handle` `get; set;` not `internal set`

### SoftProof.Tools Changes
- Add `Microsoft.Extensions.Logging.Abstractions` package
- Add `System.Windows.Media` using directive
- Fix `InverseDotGainTool`: `1.5` → `1.5f` for float literals
- Fix `MeasurementTools.cs`: rename `y` variable to `yellow` to avoid conflicts
- Fix `ConvolveFft` FFT call patterns

### SoftProof.UI/ViewModels Changes
- MainViewModel: use `RipParserRegistry` not `IRipParser` directly
- MainViewModel: add `System.IO` using for `Path.GetFileName()`, `Path.Combine`
- MainViewModel: add `System.Windows.Media` using
- MainViewModel: add `SoftProof.Core.Interfaces` and `SoftProof.Core.Models`
- MainViewModel: add `SoftProof.RIPParsers` and `SoftProof.Viewer` and `SoftProof.Tools`
- MainViewModel: implement `INotifyPropertyChanged` via `ObservableObject`
- MainViewModel: implement `ICommand` for all RelayCommands
- MainViewModel: implement `LoadRecentFiles()` using JSON file in `%APPDATA%\\SoftProof\\recent.json`
- MainViewModel: implement `AddToRecentFiles(string filePath)` same JSON approach
- MainViewModel: implement `SeparatorViewModel` with `DisplayColor` property
- MainViewModel: implement `NullToVisibilityConverter`, `BoolToTextConverter`, `ScoreToBrushConverter`, `StatusToBrushConverter`
- MainViewModel: implement `JobToTreeConverter` for TreeView data binding
- MainViewModel: implement `EnumDescriptionConverter`
- MainViewModel: implement `FileSizeConverter`
- MainViewModel: implement `InverseBoolConverter`
- MainViewModel: implement `SeparationColorToBrushConverter`

### SoftProof.UI/ViewModels/MainViewModel.cs Specific
- `private readonly RipParserRegistry _parserRegistry;` not `IRipParser`
- `private readonly IColorEngine _colorEngine;`
- `private readonly InverseDotGainTool _idgTool;`
- `private readonly DensitometerTool _densitometer;`
- `private readonly ColorMeasurementTool _colorMeasurement;`
- Constructor: `public MainViewModel(RipParserRegistry parserRegistry, IColorEngine colorEngine, InverseDotGainTool idgTool, DensitometerTool densitometer, ColorMeasurementTool colorMeasurement)`
- `LoadRecentFiles()`: JSON file in `%APPDATA%\\SoftProof\\recent.json`
- `AddToRecentFiles(string filePath)`: same JSON approach
- `LoadRecentFiles()` and `AddToRecentFiles()` replaced `Properties.Settings.Default` usage

### SoftProof.UI/Styles/PrepressColors.xaml
- Define color palette: DeepPurple primary, Lime secondary
- Material Design dark theme colors
- Status bar colors, card colors, divider colors
- Text colors primary/secondary/disabled
- Success/warning/error/info brushes

### SoftProof.UI/Styles/PrepressControls.xaml
- BoolToBrushConverter: TrueValue=AccentBrush, FalseValue=PrimaryBrush
- BoolToVisibilityConverter
- NullToVisibilityConverter
- ScoreToBrushConverter: ≥80=green, ≥60=orange, <60=red
- StatusToBrushConverter: Pending=gray, Loading=blue, Ready=green, Proofing=orange, Approved=green, Rejected=red, Error=red
- JobToTreeConverter
- BoolToTextConverter
- ScoreToBrushConverter
- StatusToBrushConverter

### SoftProof.UI/Views/MainWindow.xaml Specific Fixes
- `<Window x:Class=\"SoftProof.UI.Views.MainWindow\" ... WindowStyle=\"None\" AllowsTransparency=\"True\" Background=\"Transparent\"`
- `<local:WindowsTitleBar ... />`
- `<local:MenuBar ... />`
- `<local:QuickToolbar ... />`
- `<local:ProofCanvas ... />`
- `<local:SeparationsPanel ... />`
- `<local:InspectionToolsPanel ... />`
- `<local:DensitometerPanel ... />`
- Status bar with job info, resolution, zoom, separator combo
- Toast notification system
- Modal host
- Column resizer grips between docks
- Keyboard shortcuts: Ctrl+O, Ctrl+F, Ctrl++, Ctrl+-, Ctrl+0, Ctrl+Space, Ctrl+D, Ctrl+M, Ctrl+V, Ctrl+H, Ctrl+P, Ctrl+J, Ctrl+B, Ctrl+K, F1
- Main grid with 3-column dock layout
- Left: InspectionToolsPanel, Center: ProofCanvas + Densitometer, Right: SeparationsPanel
- Status bar at bottom

### SoftProof.UI/App.xaml Changes
- `<vm:ViewModelLocator x:Key=\"Locator\" />` in resources
- `xmlns:vm=\"clr-namespace:SoftProof.UI\"` namespace
- Remove `StartupUri=\"Views/MainWindow.xaml\"` (use DI OnStartup instead)
- Add `AppHost` property for ViewModelLocator access

### SoftProof.UI/App.xaml.cs Changes
- `private IHost? _host;` field
- `internal IHost? AppHost => _host;` property
- `protected override async void OnStartup(StartupEventArgs e)`: build host, get main window, show
- `protected override async void OnExit(EventArgs e)`: stop/dispose host
- `private static IHostBuilder CreateHostBuilder()`: register all services
- ViewModelLocator: `(Application.Current as App)?.AppHost?.Services.GetRequiredService<MainViewModel>() ?? new MainViewModel(...)`

### SoftProof.UI/ViewModels/MainViewModel.cs Specific
- `using System.IO;` for `Path.Combine`, `File.ReadAllText`
- `using System.Windows.Media;` for brushes
- `using SoftProof.Core.Interfaces;` for interfaces
- `using SoftProof.Core.Models;` for model types
- `using SoftProof.RIPParsers;` for parser types
- `using SoftProof.Viewer;` for viewer types
- `using SoftProof.Tools;` for tool types
- ObservableProperties for: CurrentJob, JobStatus, StatusText, ZoomLevel, SoftProofingEnabled, ActiveSeparation, ShowSeparations, ShowComposite, ProofSettings, LastMeasurement, StochasticAnalysis
- ObservableCollectionEx<SeparationViewModel> Separations { get; } = new()
- ObservableCollectionEx<string> RecentFiles { get; } = new()
- Constructor injection of all tools
- `LoadRecentFiles()`: JSON file in `%APPDATA%\\SoftProof\\recent.json`
- `AddToRecentFiles(string filePath)`: same JSON approach
- `SeparatorViewModel` with `Name`, `DisplayColor`, `IsVisible`, `IsSpotColor`
- `MeasurementResult` class with `Density`, `Lab`, `CMYK`, `DeltaE`, `DeltaE2000`, `DotGain`, `PrintContrast`, `HueError`, `Grayness`, `Location`, `Timestamp`
- `StochasticAnalysis` class with `Coverage`, `Granularity`, `PeakFrequency`, `SpectralUniformity`, `ClusteringIndex`, `QualityScore`, `AnalyzedAt`
- `ProofSettings` class with `DisplayProfilePath`, `SimulationProfilePath`, `RenderingIntent`, `BlackPointCompensation`, `SimulatePaperColor`, `SimulateBlackInk`, `InkLimit`, `DotGain`, `ScreenFrequency`, `ScreeningType`

## References & Support Files
- `references/prepress-config.md`: prepress configuration sheet
- `references/icc-profiles.md`: ICC profile paths and descriptions
- `references/halftone-screens.md`: AM and FM screening configurations
- `references/troubleshooting.md`: build issues, StringFormat escaping, dependency resolution
- `references/skill-rules.md`: skill library rules and conventions
- `scripts/build.ps1`: Windows PowerShell build script
- `scripts/build.bat`: Windows batch build script
- `templates/Solution.md`: solution structure documentation
- `scripts/verify-build.ps1`: build verification script

## Pitfalls & Lessons Learned

### StringFormat Escaping Pitfall
- XAML StringFormat `{0}` must be escaped as `{{0}}`
- The XAML parser interprets single braces as markup extensions
- Double braces `{{0}}` render as literal `{0}` in the output
- This applies to all StringFormat bindings in XAML files
- Never use single `{0}` in XAML StringFormat

### Project Configuration Pitfall
- MSB4121 warning indicates missing project configuration
- Ensure all .csproj files have consistent `<TargetFramework>net8.0-windows</TargetFramework>`
- Add `<OutputType>WinExe</OutputType>` for UI projects
- Clean and rebuild after configuration changes

### Dependency Vulnerabilities
- SixLabors.ImageSharp 3.1.0 has known CVEs
- Check NuGet for newer versions regularly
- Consider `dotnet list package --vulnerabilities` to monitor

### DI Container Issues
- Circular dependency errors can occur with `IEnumerable<IRipParser>`
- Register `RipParserRegistry` as both `IRipParser` and concrete type
- Avoid registering multiple implementations of the same service
- Check for duplicate service registrations in `CreateHostBuilder()`

### XAML Binding Pitfalls
- StringFormat bindings require double braces `{{0}}` not single `{0}`
- Name scopes must match between XAML and code-behind
- ResourceDictionary merging order matters for theme resources
- `x:Name` vs `Name` properties in XAML elements
- `DataContext` must be set before data binding evaluation

### Application Lifecycle Pitfalls
- `_host` must be stored as `private IHost? _host;` with `internal IHost? AppHost => _host;`
- ViewModelLocator must access `AppHost?.Services`, not `_host` directly
- OnStartup must await `_host.StartAsync()` before showing window
- OnExit must `_host.StopAsync()` and `_host.Dispose()`
- Memory leaks can occur if host not disposed on application exit

## References
- `references/prepress-config.md`: prepress configuration sheet
- `references/icc-profiles.md`: ICC profile paths and descriptions
- `references/halftone-screens.md`: AM and FM screening configurations
- `references/troubleshooting.md`: build and runtime issue index
- `references/skill-rules.md`: skill library rules and conventions

## Version History
- v1.0.0: Initial build process documentation
- v1.1.0: StringFormat escaping documentation added
- v1.1.1: DI container circular dependency fixes
- v1.1.2: XAML StringFormat guidance
- v1.2.0: DI container redesign with `AppHost` pattern
- v1.2.1: StringFormat fix guidance update
- v1.3.0: Full .NET 8 migration guide
- v1.3.1: StringFormat fix v2 with specific file patterns

## See Also
- `references/prepress-config.md`: prepress configuration details
- `references/icc-profiles.md`: ICC profile management
- `references/halftone-screens.md`: screening configurations
- `skills/`: other class-level skills
- `templates/`: starter project templates
- `scripts/`: build and verification scripts

## See Also
- `skills/`: other class-level skills
- `templates/`: starter project templates
- `scripts/`: build and verification scripts

## Contact & Maintenance
- Maintainer: .NET 8 WPF application maintainer
- Last reviewed: {DATE_PLACEHOLDER}
- Last updated: {DATE_PLACEHOLDER}
- Contact: maintainer@example.com

---
*This skill documents the .NET 8 WPF build process for the SoftProof prepress application. It is maintained as a class-level skill and updated when new build issues or workflow improvements are discovered.*