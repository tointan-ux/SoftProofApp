# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- Centralized package version management via Directory.Build.props
- EditorConfig for consistent code style across the solution
- Dependabot configuration for automated dependency updates
- Package lock files for reproducible builds
- LittleCMS (lcms2) version requirements documentation
- GitHub Actions CI workflow with build, test, and package stages
- CodeQL security analysis workflow
- MIT LICENSE file
- `docfx.json` and a real docs table of contents, so the documentation job builds
- CI gates: CS1591 documentation ratchet, and a vulnerable-package audit
- Plate resolution is read from the TIFF (XResolution/YResolution/ResolutionUnit) and carried
  per plate on `PlateLayer`
- Screening (lpi/angle) is recovered from RIP descriptions and page names where present
- Per-plate "Opaque white ink" option in the custom colour dialog: that plate composites as
  solid white over whatever is beneath it, so it stays white on a metallic or tinted substrate
  instead of being tinted by the paper
- Canvas rotation (0°/90°/180°/270°) with a toolbar control, View-menu entries and an `R`
  shortcut. It is a view transform, applied on top of per-plate flips
- Resolution badge states what is on screen: `Proxy 300 dpi`, `Full-res 2400 dpi` or
  `Refining…`, plus the rotation angle

### Fixed
- Overflow exceptions in checked context for PackBits, LZW, and TIFF strip decoders
- Bitwise NOT operations on bytes causing OverflowException
- Shift operations with zero shift count in TIFF normalization
- Test utility overflow for implausible dimension test
- All 154 tests now pass in both Debug and Release configurations
- **Colour management:** lcms2 pixel formats used the wrong bit layout (every buffer was
  declared 1-channel 16-bit); `cmsCreateProofingTransform` was declared with 9 parameters
  instead of 8; `cmsFLAGS_SOFTPROOFING` was `GRIDPOINTS(4)`; `cmsDoTransform` was treated as
  returning a success code; the proofing transform was built from a null placeholder profile
  and always threw; native profiles and transforms were never freed.
- **Measurements:** plate DPI was hardcoded to 2400 for every TIFF; barcode px→mm divided by
  mm-per-pixel instead of multiplying, so every quiet zone failed; print contrast was a constant;
  dot gain reported dot area.
- **IDG:** mechanical gain reduced dot area, optical gain was counted twice, and the pre-threshold
  curve saturated midtones to solid ink.
- **Parsers:** LZW had no output cap, tile dimensions were trusted, IFD tag data had no budget,
  and row byte counts were computed in `int`.
- **Dieline:** the dieline plate was counted as artwork, so its own line failed every knockout
  check; overprint reported a pass that was never evaluated.
- **Canvas level selection:** the viewer assumed a 1/8 pyramid base (300 dpi) and stopped
  drawing detail tiles below 75 % zoom. On plates decoded at 1/64 (37 dpi base) that stretched
  the coarsest proxy ~32x while the readout claimed 1200 dpi. The level ceiling now comes from
  the plate's own decimation factor, detail is drawn at any zoom, and the badge reports the
  resolution actually on screen.
- **Compare overlay:** 100 % was computed from the decoded base width while the pane zoom is
  relative to native pixels, so the overlay was zoomed off by the pyramid factor and could land
  on blank paper. It now uses the native plate width.

### Changed
- Zoom is capped at 100 % (one plate pixel per screen pixel); the menu offers
  Fit, 12/25/50/75/100 % and checks the current value
- Removed the duplicate "1:1 native" toolbar toggle; "1:1" is now labelled "100%"
- Proxy levels are always resampled smoothly; nearest-neighbour is reserved for the plate's
  own pixels, so a magnified overview no longer shows its decimation grid as hard blocks
- Updated all NuGet packages to latest compatible versions
- Centralized package versions (MaterialDesignThemes 4.9.0, CommunityToolkit.Mvvm 8.2.2, etc.)
- Moved from per-project package references to centralized management
- Disabled TreatWarningsAsErrors for pre-existing analyzer warnings
- IDG press simulation documented as experimental (gain magnitudes are not calibrated)
- Flip horizontal / vertical is now a per-plate switch in the plate list instead of a
  view-wide control; each ink can be mirrored independently

### Removed
- Rotation (0/90/180/270) of the view, and the `PlateOrientation` view transform with it
- Back side opacity (mirrored back-side bleed-through overlay)
- Invert / negative view of a plate (`PlateLayer.Inverted` and its per-plate toggle)

### Security
- Removed SixLabors.ImageSharp entirely: 3.1.x carries six advisories (four high severity) and
  3.2+ requires a paid Six Labors licence. Reference rasters now decode through
  System.Drawing.Common, and TIFF parsing uses the in-repo reader.
- Updated Microsoft.Extensions.* packages to 8.0.5/9.0.0
- Updated Moq to 4.20.70
- Updated xUnit to 2.8.1

## [0.1.0-preview] - 2024-10-25

### Added
- Initial release of SoftProofApp
- Harlequin Page Buffer (.pbuf, .hqn, .hpb) parser
- 1-bit TIFF separation parser (LZW, PackBits, CCITT G4, uncompressed)
- Multi-channel TIFF parser
- Esko LEN format stub parser
- LittleCMS (lcms2) integration for ICC color management
- Inverse Dot Gain (IDG) tool for stochastic screening simulation
- Densitometer tool (ANSI T, ISO T, ISO E standards)
- Color measurement and ΔE2000 verification (ISO 12647-7, G7, PSO, SWOP, GRACoL)
- High-performance WPF plate viewer with zoom, pan, rulers
- Plate pyramid cache with area-averaged downsampling
- Streaming 1-bit decode for massive plates (7+ gigapixels)
- Preflight analysis framework (polarity, content vs marks, missing separation, trap gap, tiny ink)
- Plate comparison modes (swipe, blink, difference, RIP vs reference)
- Job management with separation identification from filenames/TIFF tags
- MaterialDesign-themed WPF UI with MVVM (CommunityToolkit.Mvvm)

### Infrastructure
- .NET 8 / WPF / Windows x64
- xUnit test suite with 154 tests
- Synthetic test fixtures for TIFF/PBF formats
- libtiff oracle cross-validation tests