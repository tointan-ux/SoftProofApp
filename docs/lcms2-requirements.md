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