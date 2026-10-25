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

### Fixed
- Overflow exceptions in checked context for PackBits, LZW, and TIFF strip decoders
- Bitwise NOT operations on bytes causing OverflowException
- Shift operations with zero shift count in TIFF normalization
- Test utility overflow for implausible dimension test
- All 154 tests now pass in both Debug and Release configurations

### Changed
- Updated all NuGet packages to latest compatible versions
- Centralized package versions (MaterialDesignThemes 4.9.0, CommunityToolkit.Mvvm 8.2.2, etc.)
- Moved from per-project package references to centralized management
- Disabled TreatWarningsAsErrors for pre-existing analyzer warnings

### Security
- Updated SixLabors.ImageSharp to 3.1.3 (addresses multiple CVEs)
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