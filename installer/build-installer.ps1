param(
    [string]$Version = "1.0.0",
    [switch]$SkipPublish,
    [switch]$FrameworkDependent
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

if (-not $SkipPublish) {
    & (Join-Path $PSScriptRoot "publish-win-x64.ps1") -Version $Version -FrameworkDependent:$FrameworkDependent
    if ($LASTEXITCODE -ne 0) { throw "Publish step failed." }
}

$candidates = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) }

$iscc = $candidates | Select-Object -First 1
if (-not $iscc) {
    throw "Inno Setup 6 not found. Install it from https://jrsoftware.org/isdl.php then rerun this script. " +
          "The publish output in publish\win-x64 is ready; only the installer step is missing."
}

$dist = Join-Path $root "dist"
New-Item -ItemType Directory -Force -Path $dist | Out-Null

Write-Host "Compiling installer with $iscc ..." -ForegroundColor Cyan
& $iscc "/DMyAppVersion=$Version" (Join-Path $PSScriptRoot "SoftProof.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE." }

$setup = Join-Path $dist "SoftProof-Setup-$Version.exe"
if (-not (Test-Path $setup)) { throw "Expected installer not found: $setup" }

$sizeMb = [math]::Round((Get-Item $setup).Length / 1MB, 1)
Write-Host "Installer: $setup ($sizeMb MB)" -ForegroundColor Green
