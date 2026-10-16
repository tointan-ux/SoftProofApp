# SoftProof - Professional Prepress Soft Proofing Application

A **from-scratch, open-source** Windows application for professional prepress soft proofing, inspired by **Hamillroad FirstPROOF**. Built with modern .NET 8, WPF, and LittleCMS.

## 🎯 Features

### Core Capabilities
- **RIP File Support**: Harlequin Page Buffer (.pbuf, .hqn), 1-bit TIFF separations, multi-channel TIFF, Esko LEN
- **ICC Color Management**: Full LittleCMS (lcms2) integration for accurate color transforms
- **Soft Proofing Simulation**: Display → Print simulation with rendering intents, BPC, paper/ink simulation
- **High-Performance Viewer**: Zoom, pan, rotation, ruler overlays, multi-separation compositing

### Professional Tools (FirstPROOF Parity)
- **Inverse Dot Gain (IDG) Tool**: Hamillroad's patented stochastic/FM screening simulation
  - Point Spread Function modeling for optical dot gain
  - Tone Reproduction Curve (TRC) for paper/ink interaction
  - Spectral analysis: granularity, clustering, power spectrum
  - Quality scoring (0-100)
- **Densitometer**: ANSI T / ISO T / ISO E density standards
  - Solid density, dot area, dot gain, print contrast
  - Trapping, slur/doubling measurement
- **Color Verification**: ΔE2000 against ISO 12647-7, G7, PSO, SWOP, GRACoL tolerances
- **Job Management**: Multi-separation workflow, metadata extraction, status tracking

### Architecture
```
SoftProof.sln
├── SoftProof.Core              # Domain models, interfaces
├── SoftProof.ColorManagement   # LittleCMS wrapper (P/Invoke)
├── SoftProof.RIPParsers        # Harlequin PBF, TIFF, LEN parsers
├── SoftProof.Viewer            # WPF high-performance raster viewer
├── SoftProof.Tools             # IDG, Densitometer, Color measurement
├── SoftProof.UI                # WPF Application (MVVM, MaterialDesign)
└── SoftProof.Tests             # Unit & integration tests
```

## 🚀 Quick Start

### Prerequisites
- **.NET 8 SDK** (or later)
- **Visual Studio 2022** or **VS Code** with C# Dev Kit
- **Windows 10/11** (x64)

### Build & Run

```powershell
# Clone and build
git clone <repo>
cd SoftProofApp
.\build.ps1 -Configuration Release

# Or using batch
build.bat

# Run
src\SoftProof.UI\bin\Release\net8.0-windows\SoftProof.UI.exe
```

### Development
```powershell
# Open in Visual Studio
start SoftProof.sln

# Run tests
dotnet test SoftProof.sln -c Release

# Watch for changes (VS Code)
dotnet watch --project src/SoftProof.UI run
```

## 📦 Dependencies

### NuGet Packages
| Package | Purpose |
|---------|---------|
| `MaterialDesignThemes` | Modern WPF UI framework |
| `CommunityToolkit.Mvvm` | MVVM source generators |
| `Microsoft.Extensions.*` | DI, Logging, Hosting |
| `SixLabors.ImageSharp` | TIFF parsing |
| `MathNet.Numerics` | FFT for IDG convolution |
| `WriteableBitmapEx` | Fast bitmap manipulation |
| `xunit`, `Moq`, `FluentAssertions` | Testing |

### Native Libraries
- **lcms2.dll** (LittleCMS 2.x) - Place in `src/SoftProof.ColorManagement/runtimes/win-x64/native/`
  - Download from: https://github.com/mm2/Little-CMS/releases
  - Or build from source: https://www.littlecms.com/

## 🏗️ Architecture Details

### Color Management Pipeline
```
RIP File → Parser → Separation Data (1-bit per plate)
    ↓
Composite to CMYK → LittleCMS Transform → Simulated Print (Lab/CMYK)
    ↓                              ↑
Display Profile ←─────────────────┘ (Proofing Transform)
    ↓
Monitor Display (Soft Proof)
```

### IDG Algorithm (Inverse Dot Gain)
1. **Input**: 1-bit stochastic separation bitmap
2. **PSF Generation**: Gaussian (FM/HDS) or Directional (AM) based on screening type
3. **Convolution**: FFT-based (large kernels) or Direct (small kernels)
4. **TRC Application**: Murray-Davies + Yule-Nielsen n-factor
5. **Thresholding**: Press threshold simulation
6. **Analysis**: Spectral uniformity, clustering, granularity → Quality Score

### Supported RIP Formats
| Format | Extensions | Status |
|--------|------------|--------|
| Harlequin Page Buffer | .pbuf, .hqn, .hpb | ✅ Parser implemented |
| 1-bit TIFF Separations | .tif, .tiff | ✅ Parser implemented |
| Multi-channel TIFF | .tif, .tiff | ✅ Parser implemented |
| Esko LEN | .len, .lenx | 🔄 Stub (proprietary) |
| Presstek DI | - | 🔄 Stub |
| PDF/X | .pdf | 📋 Planned |

## 🎨 UI Overview

### Main Window Layout
```
┌─────────────────────────────────────────────────────────────┐
│ Menu | Toolbar                                              │
├──────────┬──────────────────────────┬──────────────────────┤
│ Job Mgr  │                          │ Proof Settings       │
│ Separations│     VIEWER            │ IDG Tool             │
│          │  (Zoom/Pan/Rulers)      │ Measurements         │
│          │                          │ Job Info             │
├──────────┴──────────────────────────┴──────────────────────┤
│ Status: Job | Dimensions | Resolution | Zoom | Separation  │
└─────────────────────────────────────────────────────────────┘
```

### Keyboard Shortcuts
| Shortcut | Action |
|----------|--------|
| `Ctrl+O` | Open file |
| `Ctrl+S` | Save |
| `Ctrl+P` | Print |
| `Ctrl+F` | Fit to window |
| `Ctrl++` / `Ctrl+-` | Zoom in/out |
| `Ctrl+0` | Actual size |
| `Ctrl+Space` | Toggle soft proofing |
| `Middle Mouse` / `Space+Left` | Pan |
| `Mouse Wheel` | Zoom |

## 📚 Usage Examples

### Open a Harlequin Page Buffer Job
```csharp
var parser = new HarlequinPageBufferParser(logger);
var job = await parser.ParseAsync(@"C:\Jobs\job_001.pbuf");

// Job contains separations with metadata
foreach (var sep in job.Separations)
{
    Console.WriteLine($"{sep.Name}: {sep.Color}");
}
```

### Run IDG Analysis
```csharp
var idgTool = new InverseDotGainTool(logger);
var parameters = new IdgParameters
{
    Resolution = 2400,
    ScreenFrequency = 175,
    ScreeningType = ScreeningType.HDS,
    OpticalDotGainMm = 0.03f,
    PaperScatterFactor = 1.0f
};

var simulated = idgTool.ApplyIdgSimulation(separationData, width, height, parameters);
var analysis = idgTool.AnalyzeStochasticQuality(separationData, width, height);
Console.WriteLine($"Quality: {analysis.QualityScore:F1}/100");
```

### Soft Proofing Setup
```csharp
var engine = new LittleCmsEngine(logger);
var displayProfile = await engine.LoadProfileAsync(@"C:\Profiles\Monitor.icc");
var printProfile = await engine.LoadProfileAsync(@"C:\Profiles\ISOcoated_v2.icc");

var proofSettings = new ProofSettings
{
    DisplayProfilePath = displayProfile.Path,
    SimulationProfilePath = printProfile.Path,
    RenderingIntent = RenderingIntent.RelativeColorimetric,
    BlackPointCompensation = true,
    SimulatePaperColor = true
};

var proofTransform = await engine.CreateProofTransformAsync(
    displayProfile, printProfile, proofSettings);
```

## 🔧 Configuration

### Proof Settings (ProofSettings.cs)
| Property | Default | Description |
|----------|---------|-------------|
| `RenderingIntent` | RelativeColorimetric | ICC rendering intent |
| `BlackPointCompensation` | true | Preserve shadow detail |
| `SimulatePaperColor` | true | Simulate substrate white |
| `SimulateBlackInk` | true | Simulate ink black point |
| `InkLimit` | 300% | Total ink limit |
| `DotGain` | 0% | Mechanical dot gain compensation |
| `ScreenFrequency` | 175 LPI | Screening frequency |
| `ScreeningType` | AM | AM/FM/Hybrid/HDS/Crystal/Sublima |

### IDG Parameters (IdgParameters.cs)
| Property | Typical Range | Description |
|----------|---------------|-------------|
| `OpticalDotGainMm` | 0.02-0.15 mm | Light scatter in substrate |
| `MechanicalDotGain` | 5-20% | Physical dot spread on press |
| `PaperScatterFactor` | 0.7-1.5 | Paper-specific scatter multiplier |
| `YuleNielsenN` | 1.7-2.2 | Optical gain exponent |
| `PressThreshold` | 0.4-0.6 | Press ink acceptance threshold |

## 🧪 Testing

```powershell
# Run all tests
dotnet test --filter "FullyQualifiedName~SoftProof.Tests"

# Run specific test class
dotnet test --filter "FullyQualifiedName~InverseDotGainToolTests"

# With coverage
dotnet test --collect:"XPlat Code Coverage"
```

## 📄 License

MIT License - See [LICENSE](LICENSE) for details.

## 🙏 Acknowledgments

- **Hamillroad Software** - FirstPROOF inspiration and IDG patent reference
- **LittleCMS Team** - Graeme Gill et al. for the world-class CMM
- **MaterialDesignInXamlToolkit** - Beautiful WPF theming
- **ImageSharp** - Cross-platform image processing
- **Math.NET Numerics** - Numerical computing

## 🤝 Contributing

1. Fork the repository
2. Create feature branch (`git checkout -b feature/amazing-feature`)
3. Commit changes (`git commit -m 'Add amazing feature'`)
4. Push to branch (`git push origin feature/amazing-feature`)
5. Open Pull Request

## 📞 Support

- **Issues**: GitHub Issues
- **Discussions**: GitHub Discussions
- **Documentation**: `/docs` folder

---

**Built for prepress professionals who demand color accuracy and workflow efficiency.**"# SoftProofApp" 
