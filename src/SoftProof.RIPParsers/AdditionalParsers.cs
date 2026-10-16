using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using SoftProof.Core.Interfaces;
using SoftProof.Core.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Formats.Tiff;

namespace SoftProof.RIPParsers;

/// <summary>
/// Parser for 1-bit TIFF separations (one TIFF per separation/plate)
/// </summary>
public class OneBitTiffSeparationParser : IRipParser
{
    private readonly ILogger<OneBitTiffSeparationParser> _logger;
    
    public RipFormat SupportedFormat => RipFormat.OneBitTiffSeparations;
    public string[] SupportedExtensions => new[] { ".tif", ".tiff" };
    
    public OneBitTiffSeparationParser(ILogger<OneBitTiffSeparationParser> logger)
    {
        _logger = logger;
    }
    
    public async Task<bool> CanParseAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            using var image = await Image.LoadAsync(filePath, cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }
    
    public async Task<PrintJob> ParseAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return await ParseMultipleAsync(new[] { filePath }, cancellationToken);
    }
    
    public async Task<PrintJob> ParseMultipleAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default)
    {
        var files = filePaths.ToList();
        _logger.LogInformation("Parsing {Count} TIFF separation files", files.Count);
        
        var job = new PrintJob
        {
            Name = Path.GetFileNameWithoutExtension(files[0]),
            Format = RipFormat.OneBitTiffSeparations
        };
        
        var firstFile = true;
        int separationIndex = 0;
        
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            using var image = await Image.LoadAsync<L8>(file, cancellationToken);
            
            if (firstFile)
            {
                job.Metadata.Width = image.Width;
                job.Metadata.Height = image.Height;
                job.Metadata.Resolution = 2400;
                job.Metadata.BitDepth = 1;
                job.Metadata.ColorSpace = ColorSpace.CMYK;
                firstFile = false;
            }
            
            var sepName = Path.GetFileNameWithoutExtension(file);
            var separation = new Separation
            {
                Name = sepName,
                Index = separationIndex++,
                FilePath = file,
                Color = ParseSeparationColor(sepName),
                IsSpotColor = IsSpotColorName(sepName)
            };
            
            if (separation.IsSpotColor)
            {
                separation.SpotColorName = sepName;
            }
            
            job.Separations.Add(separation);
            job.Metadata.SeparationNames.Add(sepName);
        }
        
        job.Status = JobStatus.Ready;
        _logger.LogInformation("Parsed {Count} separations: {Width}x{Height}@{Resolution}dpi",
            job.Separations.Count, job.Metadata.Width, job.Metadata.Height, job.Metadata.Resolution);
        
        return job;
    }
    
    private SeparationColor ParseSeparationColor(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.Contains("cyan") || lower.EndsWith("_c") || lower.EndsWith("-c")) return SeparationColor.ProcessCyan;
        if (lower.Contains("magenta") || lower.EndsWith("_m") || lower.EndsWith("-m")) return SeparationColor.ProcessMagenta;
        if (lower.Contains("yellow") || lower.EndsWith("_y") || lower.EndsWith("-y")) return SeparationColor.ProcessYellow;
        if (lower.Contains("black") || lower.Contains("key") || lower.EndsWith("_k") || lower.EndsWith("-k")) return SeparationColor.ProcessBlack;
        if (lower.Contains("orange") || lower.EndsWith("_o")) return SeparationColor.Orange;
        if (lower.Contains("green") || lower.EndsWith("_g")) return SeparationColor.Green;
        if (lower.Contains("violet") || lower.Contains("purple") || lower.EndsWith("_v")) return SeparationColor.Violet;
        if (lower.Contains("red") || lower.EndsWith("_r")) return SeparationColor.Red;
        if (lower.Contains("blue") || lower.EndsWith("_b")) return SeparationColor.Blue;
        if (lower.Contains("white")) return SeparationColor.White;
        if (lower.Contains("varnish") || lower.Contains("coating")) return SeparationColor.Varnish;
        if (lower.Contains("primer")) return SeparationColor.Primer;
        return SeparationColor.SpotColor;
    }
    
    private bool IsSpotColorName(string name)
    {
        var lower = name.ToLowerInvariant();
        var processColors = new[] { "cyan", "magenta", "yellow", "black", "key", "c", "m", "y", "k" };
        return !processColors.Any(pc => lower.Contains(pc) || lower.EndsWith($"_{pc}") || lower.EndsWith($"-{pc}"));
    }
}

/// <summary>
/// Parser for multi-channel TIFF (all separations in one file)
/// </summary>
public class MultiChannelTiffParser : IRipParser
{
    private readonly ILogger<MultiChannelTiffParser> _logger;
    
    public RipFormat SupportedFormat => RipFormat.MultiChannelTiff;
    public string[] SupportedExtensions => new[] { ".tif", ".tiff" };
    
    public MultiChannelTiffParser(ILogger<MultiChannelTiffParser> logger)
    {
        _logger = logger;
    }
    
    public async Task<bool> CanParseAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            using var image = await Image.LoadAsync(filePath, cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }
    
    public async Task<PrintJob> ParseAsync(string filePath, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Parsing Multi-channel TIFF: {File}", filePath);
        
        var job = new PrintJob
        {
            Name = Path.GetFileNameWithoutExtension(filePath),
            FilePath = filePath,
            Format = RipFormat.MultiChannelTiff
        };
        
        using var image = await Image.LoadAsync(filePath, cancellationToken);
        
        job.Metadata.Width = image.Width;
        job.Metadata.Height = image.Height;
        job.Metadata.Resolution = 2400;
        job.Metadata.BitDepth = 8;
        job.Metadata.ColorSpace = ColorSpace.MultiChannel;
        
        var channelCount = 4;
        
        for (int i = 0; i < channelCount; i++)
        {
            var separation = new Separation
            {
                Name = $"Channel_{i + 1}",
                Index = i,
                FilePath = filePath,
                Color = (SeparationColor)i
            };
            
            job.Separations.Add(separation);
            job.Metadata.SeparationNames.Add(separation.Name);
        }
        
        job.Status = JobStatus.Ready;
        return job;
    }
    
    public Task<PrintJob> ParseMultipleAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default)
    {
        return ParseAsync(filePaths.First(), cancellationToken);
    }
}

/// <summary>
/// Parser for Esko LEN format
/// </summary>
public class LenFileParser : IRipParser
{
    private readonly ILogger<LenFileParser> _logger;
    
    public RipFormat SupportedFormat => RipFormat.LenFile;
    public string[] SupportedExtensions => new[] { ".len", ".lenx" };
    
    public LenFileParser(ILogger<LenFileParser> logger)
    {
        _logger = logger;
    }
    
    public async Task<bool> CanParseAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var header = new byte[8];
            await fs.ReadExactlyAsync(header, cancellationToken);
            var sig = Encoding.ASCII.GetString(header);
            return sig.StartsWith("LEN") || sig.Contains("Esko");
        }
        catch
        {
            return false;
        }
    }
    
    public async Task<PrintJob> ParseAsync(string filePath, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Parsing Esko LEN file: {File}", filePath);
        
        var job = new PrintJob
        {
            Name = Path.GetFileNameWithoutExtension(filePath),
            FilePath = filePath,
            Format = RipFormat.LenFile
        };
        
        job.Metadata.RipName = "Esko Flex RIP";
        job.Metadata.Width = 1;
        job.Metadata.Height = 1;
        job.Metadata.Resolution = 2400;
        job.Status = JobStatus.Error;
        
        return job;
    }
    
    public Task<PrintJob> ParseMultipleAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default)
    {
        return ParseAsync(filePaths.First(), cancellationToken);
    }
}

/// <summary>
/// Parser registry - auto-detects and routes to appropriate parser
/// </summary>
public class RipParserRegistry : IRipParser
{
    private readonly IEnumerable<IRipParser> _parsers;
    private readonly ILogger<RipParserRegistry> _logger;
    
    public RipFormat SupportedFormat => RipFormat.Unknown;
    public string[] SupportedExtensions => _parsers.SelectMany(p => p.SupportedExtensions).Distinct().ToArray();
    
    public RipParserRegistry(IEnumerable<IRipParser> parsers, ILogger<RipParserRegistry> logger)
    {
        _parsers = parsers;
        _logger = logger;
    }
    
    public async Task<bool> CanParseAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return _parsers.Any(p => p.SupportedExtensions.Any(ext => 
            filePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase)));
    }
    
    public async Task<PrintJob> ParseAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var parser = _parsers.FirstOrDefault(p => p.SupportedExtensions.Any(ext => 
            filePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase)));
        
        if (parser == null)
            throw new NotSupportedException($"No parser for file: {filePath}");
        
        return await parser.ParseAsync(filePath, cancellationToken);
    }
    
    public async Task<PrintJob> ParseMultipleAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default)
    {
        var files = filePaths.ToList();
        if (files.Count == 0)
            throw new ArgumentException("No files supplied.", nameof(filePaths));
        
        // Route by extension, exactly like ParseAsync. (Previously the first Harlequin/1-bit
        // parser was used for ANY file set, so a set of .tif plates could be handed to the
        // Harlequin parser depending on registration order.)
        var parser = GetParserForFile(files[0])
            ?? throw new NotSupportedException($"No parser for file: {files[0]}");
        
        return await parser.ParseMultipleAsync(files, cancellationToken);
    }
    
    public IRipParser? GetParserForFormat(RipFormat format)
    {
        return _parsers.FirstOrDefault(p => p.SupportedFormat == format);
    }
    
    public IRipParser? GetParserForFile(string filePath)
    {
        return _parsers.FirstOrDefault(p => p.SupportedExtensions.Any(ext => 
            filePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase)));
    }
}
