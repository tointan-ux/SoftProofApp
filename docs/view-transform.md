# ViewTransform — one zoom space for every plate view

## Problem

The same concept ("where am I looking, and how big is a plate pixel on screen?") is
currently modelled three times, in three incompatible units:

| Where | State | 100 % means | Cap |
|---|---|---|---|
| `PlateViewer` | `Zoom` (base-pyramid level units) + `_origin` | `Zoom == _nativeFactor` | `MaxZoom = _nativeFactor` |
| `PlateCompareView` (overlay) | `_zoom` multiplier over a fit scale + pan offsets | `_imageRect.Width == _plateWidth` | hard `24` |
| side-by-side sync | copies `Zoom`/centre between two `PlateViewer`s | inherits both | inherits both |

Every zoom defect in the last weeks came from converting between those spaces: the
left pane not driving the right, the readout showing the pane while the overlay moved,
"100 %" being fit in one place and native in another, mode switches snapping to fit.

## Proposal

`SoftProof.Viewer.ViewTransform` (this branch) is the single source of truth:

* `NativeScale` — DIPs per native plate pixel (the magnification). **1.0 = 100 %, always.**
* `CentreX/CentreY` — viewport centre in native plate pixels.
* `ViewportWidth/Height` — DIP size of the surface.

It is a `readonly struct` with no WPF dependency, so the geometry is unit tested
(15 tests in this branch) instead of being verified by eye through the UI.

## Status

**Steps 1–4 done.** `PlateViewer.View` and `PlateCompareView.View` publish a `ViewTransform`,
and the status bar reads its percentage from `ViewTransform.ZoomText` instead of formatting
`NativeZoom` by hand. The constructor deliberately does **not** clamp, so a view fitted below
the 2 % floor still reports its true scale; clamping lives in `Fit`, `ZoomedAt` and
`WithNativeScale`. Verified on a 2400 dpi job: 1 % overview, 100 % native, 50 % after one
wheel notch — byte-identical strings to the previous build.

Step 2 makes `PlateViewer` *store* the camera: `_camera` is a `ViewTransform` in native plate
pixels and the base-pixel `Matrix` is derived from it (`MatrixFromCamera`), adopted after each
mutation (`SetCamera` ← `CameraFromMatrix`). The conversion is exact, which is asserted by
round-trip tests rather than by screenshots: a matrix survives camera → matrix untouched, and
re-expressing it in a resized viewport leaves the offsets identical. Two rules the draft got
wrong and this step fixes:

* `SetCamera` must measure against the **live viewport** (`ActualWidth/Height`), not the
  camera's stored size — otherwise the matrix still round-trips but the centre is meaningless.
* On resize, re-express the *same* matrix in the new frame; do not recompute the centre from
  the transform, or the artwork jumps when the window changes size.

Step 3 gives the single-canvas compare overlay (`Slide`/`Blink`/`Difference`) the same camera:
`_zoom`, `_panX` and `_panY` are gone, replaced by `_camera`; `_imageRect` is
`ImageRectFor(_camera, plateWidth, plateHeight)`, the wheel is `ZoomedAt` at the cursor, and a
middle-drag is `PannedBy`. The zoom limits stay 0.25×–24× of fit but can never exceed 1:1, and
`SetPlateWidth` keeps the scale while re-centring on the new plate. The old "view 300 dpi ·
100 % · Overview 1/8" flicker is fixed at the source: the readout now treats 100 % as native
pixels even before the detail tiles land.

Step 4 makes side-by-side sync a struct copy: `SyncCompare` calls `target.SetView(source.View)`,
and `PlateViewer.SyncCamera` (pure, tested) clamps the magnification into the target's frame.
No pane stores zoom and pan separately, so the two views cannot drift.

### Live check (2400 dpi job, 11 811 px plate)

| action | readout |
|---|---|
| open (fit) | `view 300 dpi · 1% · Overview 1/8` |
| 1:1 | `view 2400 dpi · 100% · Native pixels` — no `Overview 1/8` flash |
| Slide | `100% · Native pixels` (scale kept across the mode switch) |
| wheel ×1 / ×2 | `87%` / `75.6%` |
| middle-drag | `76%` (pan keeps the scale) |
| Difference / Blink | `76%` |
| 1:1 again | `100% · Native pixels`, both overlay sides decoded (log: `overlay native side 0/1: 1428x702 at 1:1`) |

The run found a real defect the tests could not: the overlay capped zoom at `24 × fit`, so on a
sheet whose fit is 1.75 % the ceiling was 42 % and `SetNativeZoom(1.0)` silently stopped there.
The overlay now always allows 100 %, floor still 0.25 × fit.

## Migration plan (behaviour-neutral, in this order)

1. **Introduce and use for readout only.** `PlateViewer` keeps its own `Zoom`/origin,
   but publishes `ViewTransform` derived from them; the status bar and the overlay read
   zoom from the transform. No visual change; add a test that the derived scale matches
   the existing `NativeZoom`.
2. **Make `PlateViewer` store the transform** and compute `Zoom`/origin from it at the
   edges (render path, hit-testing, detail request). Golden-image tests for the
   resampler must stay green — they already cover the pixel output.
3. **Make the overlay store one too**, replacing `_zoom`/`_panX`/`_panY`. The
   `_plateWidth`-based 100 % maths disappears; `SetNativeZoom` becomes
   `WithNativeScale` + `ClampedTo`.
4. **Sync becomes copy.** Left pane drives compare pane by copying the struct; no
   per-mode conversion, no guard flags beyond a re-entrancy check.
5. **Delete the duplicates**: `MaxZoom`/`MinZoom` constants, fit-scale helpers,
   `UpdateOverlayZoom`, the special-case in `RefreshCompare`.

Steps 1–2 can ship independently of the overlay work; each step keeps the full test
suite green (176 tests as of this branch).
