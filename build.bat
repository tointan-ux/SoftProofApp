@echo off
REM SoftProof Build Script
REM Builds the entire solution in Release mode

set SOLUTION=SoftProof.sln
set CONFIGURATION=Release
set PLATFORM=Any CPU

echo Building SoftProof...
echo Solution: %SOLUTION%
echo Configuration: %CONFIGURATION%
echo Platform: %PLATFORM%
echo.

dotnet restore %SOLUTION%
if errorlevel 1 (
    echo Restore failed!
    exit /b 1
)

dotnet build %SOLUTION% -c %CONFIGURATION% -p:Platform="%PLATFORM%" --no-restore
if errorlevel 1 (
    echo Build failed!
    exit /b 1
)

echo.
echo Build successful!
echo Output: src\SoftProof.UI\bin\%CONFIGURATION%\net8.0-windows\

REM Run tests
echo.
echo Running tests...
dotnet test %SOLUTION% -c %CONFIGURATION% --no-build
if errorlevel 1 (
    echo Tests failed!
    exit /b 1
)

echo.
echo All tests passed!