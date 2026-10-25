$content = Get-Content 'src\SoftProof.Core\Models\PrintJob.cs' -Raw

# Find the ProofSettings class start and end
$classStart = $content.IndexOf('public class ProofSettings')
$nextClass = $content.IndexOf('public sealed class PreflightSettings')

if ($classStart -lt 0 -or $nextClass -lt 0) {
    Write-Host "ERROR: Could not find ProofSettings class boundaries"
    exit 1
}

$before = $content.Substring(0, $classStart)
$after = $content.Substring($nextClass)

$newProofSettings = @'
/// <summary>
/// Soft proofing settings (simulation parameters)
/// </summary>
public class ProofSettings
{
    // Display profile (monitor)
    public string DisplayProfilePath { get; set; } = string.Empty;
    
    // Print simulation profile (target press/proof)
    public string SimulationProfilePath { get; set; } = string.Empty;
    
    // Rendering intent for simulation
    public RenderingIntent RenderingIntent { get; set; } = RenderingIntent.RelativeColorimetric;
    
    // Black point compensation
    public bool BlackPointCompensation { get; set; } = true;
    
    // Simulate paper color (white point)
    public bool SimulatePaperColor { get; set; } = true;
    
    // Simulate black ink
    public bool SimulateBlackInk { get; set; } = true;
    
    // Ink limiting
    public float InkLimit { get; set; } = 300.0f; // %
    
    // Dot gain compensation (global fallback)
    public float DotGain { get; set; } = 0.0f; // %
    
    // Screen frequency for simulation
    public float ScreenFrequency { get; set; } = 175.0f; // LPI
    
    // Screening type
    public ScreeningType ScreeningType { get; set; } = ScreeningType.AM;
    
    // Per-plate TVI / Dot Gain curves (TVI = Tone Value Increase)
    /// <summary>
    /// Per-plate TVI curves for dot gain compensation. Key = SeparationColor.
    /// If empty, standard offset curves are used.
    /// </summary>
    public Dictionary<SeparationColor, TVICurve> TVICurves { get; set; } = new();
    
    // Custom device link profile (optional)
    public string DeviceLinkProfilePath { get; set; } = string.Empty;

    /// <summary>
    /// Preflight thresholds for the press this profile describes. A web press with a wide slug
    /// band and a sheetfed press with a narrow one must not share the same numbers.
    /// </summary>
    public PreflightSettings Preflight { get; set; } = new();
}
'@

$newContent = $content.Substring(0, $classStart) + $newProofSettings + $content.Substring($content.IndexOf('public sealed class PreflightSettings'))
$newContent | Set-Content 'src\SoftProof.Core\Models\PrintJob.cs'
Write-Host 'Updated ProofSettings with TVICurves'