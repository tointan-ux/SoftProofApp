# SoftProof — Zoom issue: the viewer is stuck on the 9× decimated image

The viewer is still showing the 9× decimated image, and zooming only magnifies it. Your screenshot has three clues.

## What the evidence says

- **The status bar:** the plate is 60000×73229 px and was "decoded 9× smaller to 6667×8137 px at 266 dpi (pyramid cache)". There is only one decoded image, and it is the small one. The earlier message about a limit of about 250 million pixels per page at full resolution suggests the loader still has a hard cap, and this plate is about 4.4 Gpx, so it falls back to downsampling.
- **Pale pixels along the edge of the yellow line:** a real 1-bit plate has no in-between tones. Those lighter pixels are area-averaged coverage values produced by decimation.
- **The zoom number is relative to the decimated image.** At "601%" you are seeing about 6 screen pixels per decimated pixel. Relative to the real plate that is 6.01 ÷ 9 ≈ 0.67 screen pixels per plate pixel. At that scale you would not see individual RIP pixels even with a perfect renderer. The "Dots (1600%)" button works out to about 1.8 screen pixels per plate pixel, which is still not true dot-level inspection.

## Why this happens

The pyramid cache is built and used as the only source. The renderer has no path from a zoomed viewport back to level 0 (the real 1-bit strips), so it keeps scaling the coarse level. Most likely the viewer draws one bitmap and applies a scale transform, instead of choosing a level per zoom and fetching tiles.

## The fix

### 1. Re-base zoom on native pixels

Define `scale` as screen pixels per plate pixel at 2400 dpi, and show the percentage from that. Add a readout such as `Plate 2400 dpi · viewing level 3 (1/8)`.

### 2. Choose the pyramid level from the scale

```csharp
// scale = screen px per native plate px (1.0 = one plate pixel per screen pixel)
int level = scale >= 1.0 ? 0 : (int)Math.Floor(Math.Log2(1.0 / scale));
level = Math.Clamp(level, 0, pyramid.MaxLevel);
```

Level 0 is the original 1-bit strips and level *n* is 2ⁿ decimation. Use a power-of-two ladder instead of one 9× step.

### 3. Render level 0 from strips, not from the cache

When `level == 0`, request only the visible rows from the strip decoder, expand the visible window to 8-bit (or draw directly as 1-bit), and keep a small LRU of decoded tiles. Visible area at 100% is small (a few thousand pixels wide), so this is cheap even for a 4 Gpx plate.

### 4. Interpolation

- **Level 0 at scale ≥ 1:** nearest neighbor, so each plate pixel is a crisp block. In WPF use `RenderOptions.SetBitmapScalingMode(..., BitmapScalingMode.NearestNeighbor)`.
- **Coarser levels:** the area-averaged level for the current scale, with linear or default scaling.

### 5. Remove the cap

The 250 Mpx limit applies to full-image decode. With the strip path it should apply only to the visible tile set. Keep the downsampled path only as the overview levels.

### 6. Make the state visible

Show a badge on the canvas: `Preview 1/8` while a coarse level is drawn, `Native pixels` once level 0 is shown, and `Refining…` while tiles load.

## Quick checks to confirm the cause

- Log the pyramid level and the decoded resolution each time the view redraws. If it always reports the same level at every zoom, the renderer never switches.
- Zoom in as far as you can: if the staircase blocks keep growing in proportion and no finer structure ever appears, there is no level-0 path.
- Check where the loader decides to downsample (the 250,000,000-pixel guard), and make sure the downsample decision only affects the overview levels, not the stored plate.

## What true native scale should look like

At true native scale on this edge you should see a slightly ragged but fine edge with square 10.6 µm pixels, and with a screened plate you would see halftone dots. Real dot structure typically needs about 4 screen pixels per plate pixel or more, because at 175 lpi a cell is only about 14 plate pixels across.
