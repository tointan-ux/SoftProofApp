$content = Get-Content 'src\SoftProof.Core\Models\PrintJob.cs' -Raw

# Find the ProofSettings class and add TVICurves after DeviceLinkProfilePath
$old = '/// <summary>
    /// Custom device link profile (optional)
    /// </summary>
    public string DeviceLinkProfilePath { get; set; } = string.Empty;

    /// <summary>
    /// Preflight thresholds for the press this profile describes.'

$new = '/// <summary>
    /// Custom device link profile (optional)
    /// </summary>
    public string DeviceLinkProfilePath { get; set; } = string.Empty;

    /// <summary>
    /// Per-plate TVI (Tone Value Increase) / Dot Gain curves for dot gain compensation.
    /// Key = SeparationColor. If empty, standard offset curves are used.
    /// </summary>
    public Dictionary<SeparationColor, TVICurve> TVICurves { get; set; } = new();

    /// <summary>
    /// Preflight thresholds for the press this profile describes.'

$content = $content -replace [regex]::Escape($old), $new
$content | Set-Content 'src\SoftProof.Core\Models\PrintJob.cs'
Write-Host 'Added TVICurves to ProofSettings'