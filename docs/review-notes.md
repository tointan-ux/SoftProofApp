# SoftProof — Review Notes & Build Plan

## 1. Fix first: what the screenshot shows is broken

### Large 1-bit TIFF handling

The status bar shows the real blocker: the page is **74,646 × 97,323 px (7.26 Gpx)**, and the loader wants **6.8 GB** at one byte per pixel. Real plate files are routinely this size, so this is the core engine problem.

- Keep data **bit-packed (1 bpp)**, which is roughly **900 MB raw** for this page, and stream it instead of decoding whole pages.
- For Group 4 and LZW files, index strip/tile offsets on first open, then decode only the visible tiles on demand.
- Build an **image pyramid** using area-averaged downsampling (coverage-preserving, not nearest-neighbor) and cache it to disk, so reopening is instant.
- Keep the downsampled-load fallback, but make it an explicit **"Open at 1/4 resolution"** button in the error toast rather than a message in the status bar.
- Show a progress bar and a cancel button, and decode on worker threads.

### Separation identification

All four plates are named `"Ace Kid mod tuck in DE…"` with the same pink swatch, so the app can't tell which plate is which. That's why **Screen Angle Separation** and **Total Ink Coverage** can't run. Add:

- Name parsing from filename suffixes (`_C`, `.Cyan`, `(C)01`), TIFF `ImageDescription` tags, and sidecar metadata.
- A manual **"assign separation"** dropdown per plate, with ink name, CMYK/spot, and a Lab value for spots.
- Truncated names with a tooltip, plus the full filename in the inspector.

## 2. Core soft-proofing features

- **Accurate composite simulation:** apply plate dot gain/TVI curves, ICC output profiles (FOGRA39/51, GRACoL), paper white, and ink-set simulation, so the composite approximates press output.
- **Overprint and spot simulation:** spot inks via Lab, overprint preview for varnish, white, and metallics (relevant for the holobox job).
- **Zoom-aware rendering:** area-averaged tone at low zoom, and exact dot structure at high zoom (the "Dots 1600%" mode). Add a pixel grid at extreme zoom and a toggle between "halftone dots" and "simulated continuous tone."
- **Film/emulsion options:** negative/positive, emulsion up/down, mirror/flip, and a right-reading toggle for flexo and gravure.
- **Color-neutral viewing environment:** use a neutral gray surround around the canvas. The blue-tinted dark UI skews color judgment.
- **Calibrated display support:** read the monitor ICC profile and warn if the display isn't profiled.

## 3. Measurement and inspection

- **Densitometer:** show dot % per plate under the cursor, aperture averaging (1×1 / 3×3 / 5×5), and predicted tone value after the dot gain curve. Add a pinned multi-point readout table.
- **Screen analysis:** detect lpi and angle per plate with an FFT, and verify against the expected angles (e.g. C 15°, M 75°, Y 0°, K 45°). Flag moiré and rosette problems.
- **Total area coverage:** a TAC heatmap with a threshold overlay (e.g. highlight pixels over 300%) and a max-TAC readout with its location.
- **Ink coverage by zone:** a per-ink-key-zone bar chart, with CIP3/PPF export for press presetting.
- **Highlight/shadow dot checks:** minimum dot size and highlight dot loss, with the failing areas highlighted and click-to-locate.
- **Control strip reader:** measure step wedges and patches (Fogra, Ugra/Brunner), then compare against target values and tolerances.
- **Caliper upgrades:** snap to edges, lock to horizontal/vertical, show measurements in mm, pt, and px, and keep a saved measurement list.
- **Trap and registration:** measure gaps and overlaps between plates, and detect hairline and minimum line/text width failures.

## 4. Preflight as real, actionable checks

The five existing checks are good, but they are all unimplemented. Make each one:

- **Configurable** with a threshold set per press, substrate, and process (flexo, offset, gravure).
- **Locatable:** each failure is a click target that zooms to the issue, plus an overlay layer.
- **Exportable** into the sign-off report.

Barcode quiet zone and BWR (bar width reduction) checks are especially valuable for packaging, along with dieline knockout and overprint. Add minimum text size, a registration mark presence check, and a "spot ink used but not defined" check.

## 5. Dieline and packaging tools

- Import dielines from PDF, DXF, or CF2 with a layer toggle.
- Show bleed, safe zone, glue flaps, and varnish-free areas.
- Check knockouts and overprint against die-cut paths.
- Offer a "mask outside dieline" view.
- Run fold simulation or 3D mock-up, which is a differentiator for carton work.

## 6. Compare and revision control

- Plate-to-plate and revision-to-revision compare with swipe, blink, and difference modes.
- Compare RIP output to a rasterized reference PDF to catch RIP errors.
- Side-by-side view for two plates or two jobs, with synchronized zoom and pan.

## 7. Sign-off and workflow

- **Audit trail:** the sign-off should capture user, timestamp, file hashes, preflight results, measurements, and annotated screenshots, exported as a PDF report.
- **Annotations and comments:** pins with notes, plus approve/reject per plate.
- **Integration:** hot-folder or watch-folder auto-open, JDF/JMF status, and a command-line mode for batch checks.
- **Multi-page and imposition navigation:** page list, sheet thumbnails, and jump-to-plate.
- **Session persistence:** remember zoom, measurements, and layout per job.

## 8. UI polish

- Show an empty plate area (0.0%) as "not computed" until analysis runs, because 0.0% reads as a real result.
- Add keyboard shortcuts everywhere (M for measure already exists) and show them in tooltips.
- Add a recent files list, drag-and-drop open, and a plate solo/mute toggle with hotkeys 1–4.
- Show engine/status info (the "Engine: --" field) with decode time, memory use, and pyramid cache state.

## Suggested build order

| # | Step | Why |
|---|------|-----|
| 1 | Streaming tiled 1-bit decoder + pyramid cache | Unblocks everything |
| 2 | Separation naming and assignment | Gates screen-angle and TAC checks |
| 3 | Plate inked area, TAC, and a real dot-percent densitometer | First real measurements |
| 4 | Implement the five preflight checks | Turn stubs into product |
| 5 | Soft proof color pipeline (ICC + dot gain) | The actual "soft proof" |
| 6 | Compare modes and the sign-off report | Workflow wedge |
| 7 | Dieline and packaging extras | Differentiator |

---

# Implementation plan: .NET 8 + WPF + LittleCMS

Structure the tile-streaming, pyramid, and color pipeline on **.NET 8 + WPF + LittleCMS**.

## Project layout

| Project | Responsibility |
|---------|----------------|
| **Core** (no UI) | TIFF indexer/decoder, pyramid builder, tile cache, analysis (coverage, TAC, FFT). Keep it testable with no WPF dependency. |
| **Color** | Thin P/Invoke wrapper over `lcms2` (profiles, transforms, proofing). |
| **App** (WPF, MVVM) | `CommunityToolkit.Mvvm` works well. The viewer is a custom control, **not** an `Image`. |

## 1. Streaming 1-bit decoder

- RIP output is usually **stripped, not tiled**. G4 and LZW strips decode independently, so on open you only read the IFD and build a strip offset/length table. That takes milliseconds even for 97k rows.
- Decode strips on demand into `byte[]` rows of 1 bpp, using `ArrayPool<byte>`, `Span<T>`, and `MemoryMappedFile` views of the source file.
- Use a library for G4/LZW rather than writing your own. A managed TIFF library or native libtiff via P/Invoke both work. Benchmark both on your largest file, since G4 decode speed matters here.
- Decode on a bounded worker pool (`System.Threading.Channels` with a priority for strips in the current viewport), and pass a `CancellationToken` through everything so panning cancels stale work.

## 2. Pyramid cache

- Build it in one streaming pass: decode a strip, popcount 2×2 / 4×4 / 8×8 blocks, and accumulate into 8-bit coverage tiles. `BitOperations.PopCount` and `System.Runtime.Intrinsics` make this fast.
- Store level 0 as the original 1 bpp (render straight from strips). Store levels 1+ as 8-bit coverage tiles (e.g. 256×256) in a sidecar file keyed by **file hash + mtime**, memory-mapped on reopen.
- Rough sizes for your 7.26 Gpx page: 1/8 scale is about **113 MB** at 8 bpp, and 1/16 is about **28 MB**. Skip persisting the biggest levels if the build time is short, and cache only 1/8 and below.
- Keep an in-memory LRU of decoded tiles with a byte budget (for example 512 MB), and show the budget and hit rate in the "Engine:" field.

## 3. Rendering in WPF

WPF's built-in imaging does not handle this well, so don't push huge `BitmapSource`s through it.

- Write a custom `FrameworkElement` that overrides `OnRender`, picks the pyramid level for the current zoom, and draws only visible tiles as small `WriteableBitmap`s (or use SkiaSharp's `SKElement` if you want faster compositing and easy overlays).
- Draw in this order: composite tiles → overlays (TAC heatmap, preflight failures, dieline) → measurement graphics. Keep overlays as separate layers so they can toggle without re-rendering tiles.
- At dot-level zoom (≥ 800%), decode level 0 to 8-bit per plate only for visible strips, nearest-neighbor scaled with no smoothing.

## 4. Color pipeline with LittleCMS

- **CMYK plates to screen:** build each tile as interleaved CMYK 8-bit from the four plate tiles, then run one `cmsDoTransform` per tile.
- **Soft proof:** use a proofing transform (`cmsCreateProofingTransform`) with the press/paper profile as the proof profile and the monitor profile as output. Expose intent, black point compensation, and "simulate paper white" as toggles.
- **Dot gain/TVI:** apply per-plate tone curves to the plate coverage *before* the transform, so the plate data is adjusted rather than the profile.
- **Spots:** LittleCMS is CMYK/RGB-centric. For spot inks, convert each spot's Lab (and an opacity/overprint rule) to display RGB yourself and blend over the process composite. Treat this as an approximation and label it clearly.
- **Display profile:** read the monitor profile via the Windows color management API (`GetICMProfile` on the display DC), then re-read it when the window moves between monitors.
- **Cache transforms** (one per intent/profile combination), because creating them is expensive and running them is cheap.

## 5. Dot-accurate densitometer

At 2400 dpi, a 1×1 / 3×3 / 5×5 pixel aperture covers only a tiny part of one halftone cell, so it would return near-random values. At 175 lpi, one cell is about **13.7 pixels** across. Define apertures in physical terms (e.g. 0.5, 1, 2 mm, or N halftone cells) so the dot-percent reading averages over enough cells to be stable. Read the aperture directly from level-0 strips using popcount, so it stays exact regardless of zoom.

## 6. Analysis jobs

- Run plate inked area, TAC, and screen analysis as background jobs that stream strips once, so the first three can share a single pass.
- For lpi/angle detection, take an FFT of sampled flat-tint regions per plate. `MathNet.Numerics` or a native FFT library would do the job. Verify against the expected angles, and flag anything outside a tolerance.
- Expose results through an `IProgress<T>` so the plate panel can show "computing…" instead of the misleading 0.0%.

## 7. Memory and stability

- Cap the total in-memory footprint explicitly. At this file size, the main risk is not CPU but memory pressure from four plates open at once.
- Put pyramid building behind a progress dialog with cancel, since the first open will take longer than later ones.
- Log decode time per strip, so you can find slow files and slow disks.
