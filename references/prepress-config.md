# prepress-config.md

## Prepress Configuration Sheet

### ICC Profile Paths
- Display Profile: `C:\Profiles\Monitor\ICC\monitor.icc`
- Simulation Profile: `C:\Profiles\Press\ISOcoated_v2.icc`
- Display Profile (Alternative): `C:\Profiles\sRGB\display.icc`
- Simulation Profile (Alternative): `C:\Profiles\Press\GRACoL.icc`

### Screen Parameters
- Screen Frequency: `175 LPI` (typical)
- Screen Angles: Cyan=15°, Magenta=75°, Yellow=0°, Black=45°
- Screen Type: `AM Halftone (Euclidean)` or `FM/HDS Stochastic`
- Dot Gain: `10-15%` mechanical + `15-20%` optical
- Yule-Nielsen n-factor: `2.0` typical

### Press Settings
- Target Press: `Sheetfed Offset Commercial`
- Ink Limit: `300%` total
- Paper Type: `Coated` (typical), `Uncoated`, `Newsprint`
- Paper Scatter Factor: `1.0` (coated), `1.3-1.5` (uncoated)
- Yule-Nielsen n-factor: `1.7-2.2` range
- Press Threshold: `0.4-0.6` range

### Color Space
- Color Space: `CMYK Process` or `CMYK + Spots`
- ICC Profile: `ISOcoated_v2_eci` or custom profiles
- Artwork Type: `packaging_carton`, `offset_spread`, `label`, `flexo`

### File Processing
- Supported Extensions: `.pbuf`, `.hqn`, `.tif`, `.tiff`, `.len`, `.lenx`
- Resolution: `2400 DPI` typical
- Plate Width: `720 mm` or `500 mm`
- Plate Height: `1020 mm` or `700 mm`
- Plate Resolution: `2400 DPI`
- Bit Depth: `1` for 1-bit separations, `8` for multi-channel

### Halftone Parameters
- Screen Type: `AM Halftone (Euclidean)`, `FM/HDS Stochastic`, `Hybrid`, `Crystal`, `Sublima`
- Screen Angle: `0-45` degrees (typically 45°)
- Screen Ruling: `175 LPI` typical
- Dot Shape: `Euclidean`, `Round`, `Crisp`
- Halftone Simulator: `Hamillroad IDG`, custom PSF, TRC

### Measurement Parameters
- Densitometer Standard: `ISO 12647-7`, `G7`, `PSO`, `SWOP`, `GRACoL`
- Densitometer: `ISO 12647-2` compatible
- Color Verification: `ΔE00 ≤ 2.5` (ISO 12647-7), `ΔE00 ≤ 3.0` (G7)
- Tolerance Standards: `ISO 12647-7`, `G7`, `PSO`, `SWOP`, `GRACoL`
- Measurement Distance: `mm` units
- Density Range: `0.0-3.0` typical
- Dot Gain Measurement: `%` of theoretical dot area
- Print Contrast: `(Ds - Dt) / Ds` formula

### Hot Folder Configuration
- Hot Folder Path: `C:\HotFolders\Incoming`
- Processed Folder: `C:\HotFolders\Processed`
- Error Folder: `C:\HotFolders\Errors`
- File Extensions: `.tif`, `.tiff`, `.len`, `.lenx`
- Auto-Process: `true` (optional)
- Auto-Notify: `true` (optional)
- Error Email: `prepress@company.com` (optional)

### Batch Preflight
- Max Files per Batch: `50` typical
- Continue on Error: `false` (stop on first error)
- Generate Report: `true`
- Report Format: `PDF` or `XML`
- Include Separation Details: `true`

### Press Proof Comparison
- Comparison Mode: `Rev A vs Rev B`
- Wipe Percentage: `0-100%` adjustable
- Focus Region: adjustable bounds on canvas
- Comparison Badges: `Rev A (Previous)`, `Rev B (Current)`

### Client Review
- Client Approval Status: `pending`, `approved`, `rejected`
- Feedback Pins: placed on canvas
- Client Review Status: tracked per job
- Comments: author, role, text, resolved flag

### Sign-Off Report
- Report Format: `PDF` or `HTML`
- Include: job info, separations, measurements, compliance status
- Sign-off Fields: signatory name, date, title
- Distribution: email, network share, portal

---

*Prepress configuration reference sheet. Update as press configurations change.*