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

* `NativeScale` — native plate pixels per DIP. **1.0 = 100 %, always.**
* `CentreX/CentreY` — viewport centre in native plate pixels.
* `ViewportWidth/Height` — DIP size of the surface.

It is a `readonly struct` with no WPF dependency, so the geometry is unit tested
(15 tests in this branch) instead of being verified by eye through the UI.

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
