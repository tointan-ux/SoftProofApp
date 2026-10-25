$content = Get-Content 'SoftProofApp\PrintJob_temp.cs' -Raw
# Extract the ProofSettings class
$start = $content.IndexOf('public class ProofSettings')
$end = $content.IndexOf('public sealed class PreflightSettings')
$proofSettings = $content.Substring($start, $end - $start)
$proofSettings | Set-Content 'SoftProofApp\ProofSettings_class.txt'
Write-Host 'Extracted ProofSettings class'