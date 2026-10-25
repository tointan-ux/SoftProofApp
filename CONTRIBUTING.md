# Contributing to SoftProofApp

Thank you for your interest in contributing to SoftProofApp! This document outlines the process for contributing code, reporting issues, and improving documentation.

## Code of Conduct

By participating in this project, you agree to abide by our [Code of Conduct](CODE_OF_CONDUCT.md). Please read it before contributing.

## How to Contribute

### Reporting Bugs

1. **Search existing issues** first to avoid duplicates
2. **Create a new issue** using the bug report template
3. **Include**:
   - Clear, descriptive title
   - Steps to reproduce
   - Expected vs actual behavior
   - Environment (OS, .NET version, Visual Studio version)
   - Sample files (if applicable, redacted)
   - Stack traces or error messages

### Suggesting Features

1. **Check the roadmap** in `docs/review-notes.md` and existing issues
2. **Open a feature request** with:
   - Use case description
   - Proposed API/UX
   - Priority and rationale
   - Willingness to implement

### Pull Request Process

1. **Fork the repository** and create a feature branch
2. **Make your changes** following the guidelines below
3. **Run tests locally** before submitting
4. **Submit a PR** with:
   - Clear description of changes
   - Link to related issue(s)
   - Screenshots (for UI changes)
   - Updated documentation (if applicable)

## Development Setup

### Prerequisites

- **Windows 10/11** (x64)
- **.NET 8 SDK** (8.0.400 or later)
- **Visual Studio 2022** (17.8+) or **VS Code** with C# Dev Kit
- **LittleCMS (lcms2.dll)** - see `docs/lcms2-requirements.md`

### Building

```bash
# Clone your fork
git clone https://github.com/YOUR-USERNAME/SoftProofApp.git
cd SoftProofApp

# Build and test
.\build.ps1 -Configuration Release

# Or using dotnet directly
dotnet build SoftProof.sln -c Release
dotnet test SoftProof.sln -c Release
```

### Running the Application

```bash
dotnet run --project src/SoftProof.UI/SoftProof.UI.csproj -c Release
```

## Code Style Guidelines

### General Principles

- Follow the [.NET Runtime Code Style](https://github.com/dotnet/runtime/blob/main/docs/coding-guidelines/coding-style.md)
- Use **EditorConfig** (`.editorconfig`) - IDE will auto-apply settings
- Write **self-documenting code** with meaningful names
- Prefer **explicit over implicit** types where clarity improves
- Keep methods **small and focused** (single responsibility)

### C# Specific

```csharp
// ✓ Good: Explicit type for clarity
var separations = new List<Separation>();

// ✓ Good: var when type is obvious
var job = parser.ParseAsync(path);

// ✓ Good: Expression-bodied members for simple logic
public int Count => _items.Count;

// ✓ Good: Nullable reference types enabled
public Separation? FindSeparation(string name) { ... }

// ✗ Avoid: Unnecessary async/await
public async Task<Job> LoadAsync() => await Task.FromResult(Load());

// ✗ Avoid: Multiple statements on one line
if (x > 0) DoSomething(); else DoOther();
```

### Naming Conventions

| Element | Convention | Example |
|---------|------------|---------|
| Classes, Interfaces, Enums | PascalCase | `PrintJob`, `IRipParser`, `RenderingIntent` |
| Methods, Properties, Events | PascalCase | `ParseAsync`, `Separations`, `JobLoaded` |
| Private Fields | camelCase + underscore | `_logger`, `_cache` |
| Local Variables, Parameters | camelCase | `filePath`, `maxPixels` |
| Constants | PascalCase | `MaxPixelsPerPage` |
| Type Parameters | PascalCase + T prefix | `TResult`, `TKey` |

### File Organization

- One type per file (match filename to type name)
- Group related types in folders/namespaces
- Place interfaces in `Interfaces` folder
- Place DTOs/records in `Models` folder

### Documentation

- **Public APIs**: XML documentation required (`/// <summary>`)
- **Complex logic**: Inline comments explaining *why*, not *what*
- **Architecture decisions**: Document in `/docs` or as code comments
- **Breaking changes**: Note in PR description and CHANGELOG.md

## Testing Requirements

### Test Categories

| Category | Location | Purpose |
|----------|----------|---------|
| Unit Tests | `src/SoftProof.Tests/` | Isolated logic verification |
| Integration Tests | `src/SoftProof.Tests/Raster/` | Parser/decoder validation |
| Oracle Tests | `src/SoftProof.Tests/Raster/LibTiffOracleTests.cs` | Cross-check with libtiff |

### Running Tests

```bash
# All tests
dotnet test SoftProof.sln -c Release

# Specific test class
dotnet test --filter "FullyQualifiedName~LargeBilevelTests"

# With coverage
dotnet test --collect:"XPlat Code Coverage"
```

### Test Guidelines

- **AAA Pattern**: Arrange, Act, Assert
- **Descriptive names**: `Method_Scenario_ExpectedResult`
- **One assert per test** (preferred)
- **Use fixtures** for shared test data (`Fixtures/` folder)
- **No external dependencies** (file system, network) in unit tests

## Architecture Overview

```
SoftProof.sln
├── SoftProof.Core           # Domain models, interfaces, analysis
├── SoftProof.ColorManagement# LittleCMS P/Invoke wrapper
├── SoftProof.RIPParsers     # Harlequin PBF, TIFF, LEN parsers
├── SoftProof.Viewer         # High-performance WPF raster viewer
├── SoftProof.Tools          # IDG, Densitometer, Color measurement
├── SoftProof.UI             # WPF MVVM Application
└── SoftProof.Tests          # Unit & integration tests
```

### Key Interfaces

- `IRipParser` - File format parsers
- `IColorEngine` - ICC profile/transform operations
- `IPlateSource` - Streaming plate data access
- `IPreflightAnalyzer` - Preflight check implementations

## Commit Message Format

Follow [Conventional Commits](https://www.conventionalcommits.org/):

```
<type>(<scope>): <description>

[optional body]

[optional footer]
```

Types: `feat`, `fix`, `docs`, `style`, `refactor`, `test`, `chore`, `perf`, `ci`

Examples:
```
feat(parser): add support for Esko LEN format
fix(viewer): correct zoom level calculation at high DPI
docs(readme): update keyboard shortcuts table
test(idg): add edge case for zero-coverage plates
```

## Review Criteria

PRs are reviewed for:

- ✅ **Correctness**: Solves the stated problem
- ✅ **Tests**: New/updated tests pass
- ✅ **Style**: Follows code conventions
- ✅ **Docs**: Public APIs documented, CHANGELOG updated
- ✅ **Performance**: No regressions in hot paths
- ✅ **Security**: No vulnerabilities introduced

## Release Process

1. Maintainer creates release branch
2. Version bump in `Directory.Build.props`
3. CHANGELOG.md finalized
4. GitHub Release created with artifacts
5. NuGet packages published (future)

## Getting Help

- **Issues**: GitHub Issues for bugs/features
- **Discussions**: GitHub Discussions for questions
- **Discord**: (future) Community chat

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE).