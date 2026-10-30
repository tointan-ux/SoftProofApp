# LittleCMS (lcms2) Native Dependency

## Version Requirements

SoftProofApp requires **LittleCMS 2.15 or later** (lcms2.dll).

- **Minimum**: 2.12 (for basic ICC profile support)
- **Recommended**: 2.15+ (for full CIE2000 DeltaE support and proofing transforms)
- **Tested with**: 2.16 (latest as of 2024)

## Obtaining lcms2.dll

### Option 1: Build from Source (Recommended for Reproducibility)

```bash
# Clone LittleCMS
git clone https://github.com/mm2/Little-CMS.git
cd Little-CMS

# Build for Windows x64
cmake -B build -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=ON
cmake --build build --config Release

# Copy lcms2.dll to SoftProofApp
copy build\bin\Release\lcms2.dll ..\SoftProofApp\src\SoftProof.ColorManagement\runtimes\win-x64\native\
```

### Option 2: Download Pre-built Binary

1. Visit: https://github.com/mm2/Little-CMS/releases
2. Download the latest `lcms2-<version>-win64.zip`
3. Extract `lcms2.dll` and place it in:
   ```
   SoftProofApp/src/SoftProof.ColorManagement/runtimes/win-x64/native/lcms2.dll
   ```

### Option 3: NuGet Package (Alternative)

For easier dependency management, consider using the **LittleCMS.Native** NuGet package:

```xml
<PackageReference Include="LittleCMS.Native" Version="2.16.0" />
```

This package:
- Includes pre-built lcms2.dll for win-x64, linux-x64, osx-x64
- Automatically copies the native library to the output directory
- Handles platform-specific loading via .NET's native library resolver

**To migrate to LittleCMS.Native:**

1. Remove the manual `lcms2.dll` file and `<None Update="runtimes\win-x64\native\lcms2.dll">` from `SoftProof.ColorManagement.csproj`
2. Add the NuGet reference:
   ```xml
   <PackageReference Include="LittleCMS.Native" Version="2.16.0" />
   ```
3. Update the `DllImportResolver` in `LittleCmsEngine.cs` to use the NuGet package's path

## Current Configuration

The project currently uses manual placement:
```xml
<ItemGroup>
  <None Update="runtimes\win-x64\native\lcms2.dll">
    <Link>lcms2.dll</Link>
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </None>
</ItemGroup>
```

The `LittleCmsEngine.cs` uses a custom `DllImportResolver` that searches:
1. Application base directory (`lcms2.dll`)
2. `runtimes/win-x64/native/lcms2.dll`

## Verification

After placing lcms2.dll, verify it loads correctly:
```bash
dotnet run --project src/SoftProof.UI/SoftProof.UI.csproj
```

The app should start without "Could not load lcms2.dll" errors.

## CI/CD Integration

The GitHub Actions workflow should restore the native library. For the manual approach, add a build step:

```yaml
- name: Setup LittleCMS
  run: |
    # Download and extract lcms2.dll
    curl -L -o lcms2.zip https://github.com/mm2/Little-CMS/releases/download/v2.16/lcms2-2.16-win64.zip
    unzip lcms2.zip -d lcms2
    copy lcms2\lcms2.dll src\SoftProof.ColorManagement\runtimes\win-x64\native\
```

Or use the NuGet package approach which handles this automatically.

## Soft-proof pipeline (LittleCMS interop)

`LittleCmsEngine.CreateProofTransformAsync` builds the soft proof as LittleCMS intends:

| role | profile | format |
|---|---|---|
| input (document) | press profile of the plate data (defaults to the simulation profile) | CMYK 8-bit |
| output | display / monitor profile | RGB 8-bit |
| proofing | press simulation profile | — (lcms derives it) |

Four interop defects had to be fixed before any of this could run:

1. `cmsCreateProofingTransform` was declared with an extra `proofingFormat` argument. lcms does not
   take one — the extra parameter shifted `intent`, `proofingIntent` and `flags`, and the call
   failed with `cmsERROR_NOT_SUITABLE` ("Couldn't link the profiles") for every profile pair.
2. `cmsDoTransform` was declared as returning `int` and checked with `result == 0`. It returns
   `void`, so every transform "failed" after doing its work.
3. The type formatter table was malformed: `CHANNELS_SH` sat at bit 8 (lcms uses bit 3),
   `BYTES_SH(2)` was treated as 16-bit (it means 2 bytes/sample) and a stray `OPTIMIZED_SH` bit was
   OR-ed in. Formatters are now `COLORSPACE_SH(pt) | CHANNELS_SH(n) | BYTES_SH(1)`.
4. The flag table was shifted four bits: `cmsFLAGS_SOFTPROOFING` was sent as `0x40000` instead of
   `0x4000`.

Additionally the transform roles were inverted (simulation profile as *output*, display profile as
the *proofing* profile), a second transform was built from an `IntPtr.Zero` handle and never used,
and the TVI/ink-limit stages were constructed twice.

`ProofTransform.ProofingTransform` is the one-pass composite and is what
`ApplyProofTransformAsync` now uses (`document → display`, proofed, with paper-white simulation
expressed as the proofing intent `AbsoluteColorimetric`). The two legs remain available as
`SimulationTransform` + `DisplayTransform` for callers that need the intermediate press-space frame.

LittleCMS errors are surfaced: `LcmsNative` installs a log handler and every failure message
includes `lcms <code>: <text>`.

### Bit depth is part of the formatter

`FormatterFor` builds every lcms TYPE from the buffer the application actually moves. The integer
formatter is **8 bits per sample** (`BYTES_SH(1)`), because plates and previews are `byte[]`
(`PixelFormat.CMYK32`, `BGRA32`). A 16-bit formatter over an 8-bit buffer makes `cmsDoTransform`
read and write past both arrays — silent corruption, or an access violation on small images.

Destination formats match: `GetOutputFormat` returns `Gray8`/`RGB24`/`CMYK32`/`Lab32`, and the
stride pads rows to four bytes. A profile whose colour space has no formatter (multi-channel,
device link) is rejected at transform creation with `NotSupportedException` instead of being
silently treated as CMYK.

Guarded by `FormatterBitDepthTests`: the exact TYPE values for Gray/RGB/CMYK/Lab, the float
variant, the unsupported-space error, and an 8-bit round trip whose destination stride proves the
transform never allocated 16-bit rows.
