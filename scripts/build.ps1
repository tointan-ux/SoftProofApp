<# 
.SYNOPSIS
    Build script for SoftProof - Professional Prepress Soft Proofing Application

.DESCRIPTION
    Restores, builds, and tests the SoftProof solution.
    
.PARAMETER Configuration
    Build configuration (Debug/Release). Default: Release
    
.PARAMETER RunTests
    Whether to run unit tests. Default: $true
    
.PARAMETER Clean
    Whether to clean before building. Default: $false

.EXAMPLE
    .\build.ps1 -Configuration Release -RunTests $true
#>

param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    
    [switch]$RunTests = $true,
    
    [switch]$Clean = $false
)

$Solution = "SoftProof.sln"
$ProjectRoot = Split-Path -Parent $MyInvocation.MyCommand.Definition

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "SoftProof Build Script" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "Solution: $Solution"
Write-Host "Configuration: $Configuration"
Write-Host "Run Tests: $RunTests"
Write-Host "Clean: $Clean"
Write-Host ""

Set-Location $ProjectRoot

function Execute-Command {
    param([string]$Command, [string]$Description)
    Write-Host ">>> $Description" -ForegroundColor Yellow
    Write-Host "    $Command" -ForegroundColor Gray
    # Stream output to the console (previously captured into $result, hiding build/test output).
    Invoke-Expression $Command | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $Description" -ForegroundColor Red
        exit $LASTEXITCODE
    }
    Write-Host "OK: $Description" -ForegroundColor Green
    Write-Host ""
}

if ($Clean) {
    Execute-Command "dotnet clean $Solution -c $Configuration" "Cleaning solution"
}

Execute-Command "dotnet restore $Solution" "Restoring packages"
Execute-Command "dotnet build $Solution -c $Configuration --no-restore" "Building solution"

if ($RunTests) {
    # Single quotes keep "console;verbosity=normal" as ONE argument (the ';' would otherwise split it).
    # (\" is bash-style escaping; PowerShell uses a backtick, so it broke the string.)
    Execute-Command "dotnet test $Solution -c $Configuration --no-build --logger 'console;verbosity=normal'" "Running tests"
}

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "BUILD COMPLETE" -ForegroundColor Green
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "Output: src\SoftProof.UI\bin\$Configuration\net8.0-windows\" -ForegroundColor Cyan