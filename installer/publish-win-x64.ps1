param(
    [string]$Configuration = "Release",
    [string]$Version = "1.0.0",
    [string]$Output = "",
    [switch]$FrameworkDependent
)

$ErrorActionPreference = "Stop"

$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root "src\SoftProof.UI\SoftProof.UI.csproj"
if (-not $Output) { $Output = Join-Path $root "publish\win-x64" }

$dotnet = (Get-Command dotnet.exe -ErrorAction SilentlyContinue)
if ($dotnet) {
    $dotnetPath = $dotnet.Source
} else {
    $dotnetPath = "C:\Program Files\dotnet\dotnet.exe"
}
if (-not (Test-Path $dotnetPath)) {
    throw "dotnet not found. Install the .NET 8 SDK or pass a path on PATH."
}

if (Test-Path $Output) {
    Remove-Item $Output -Recurse -Force
}

$selfContained = if ($FrameworkDependent) { "false" } else { "true" }

Write-Host "Publishing SoftProof $Version (win-x64, self-contained=$selfContained)..." -ForegroundColor Cyan

& $dotnetPath publish $project `
    -c $Configuration `
    -r win-x64 `
    --self-contained $selfContained `
    -p:Platform="Any CPU" `
    -p:PublishReadyToRun=true `
    -p:DebugType=none `
    -p:Version=$Version `
    -o $Output `
    --nologo

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$exe = Join-Path $Output "SoftProof.exe"
$lcms = Join-Path $Output "lcms2.dll"

if (-not (Test-Path $exe)) { throw "Publish output is missing SoftProof.exe." }
if (-not (Test-Path $lcms)) { throw "Publish output is missing lcms2.dll (native LittleCMS)." }

$sizeMb = [math]::Round(((Get-ChildItem $Output -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host "Published to $Output ($sizeMb MB)" -ForegroundColor Green
Write-Host "Verified: SoftProof.exe, lcms2.dll" -ForegroundColor Green
