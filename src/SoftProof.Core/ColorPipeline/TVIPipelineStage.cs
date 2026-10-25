using SoftProof.Core.Models;

namespace SoftProof.Core.ColorPipeline;

/// <summary>
/// Pipeline stage for applying TVI curves before ICC transform
/// </summary>
public sealed class TVIPipelineStage
{
    private readonly Dictionary<SeparationColor, TVICurve> _tviCurves;
    private readonly int _channels;
    
    public TVIPipelineStage(Dictionary<SeparationColor, TVICurve> tviCurves, int channels = 4)
    {
        _tviCurves = tviCurves ?? new Dictionary<SeparationColor, TVICurve>();
        _channels = channels;
    }
    
    /// <summary>
    /// Applies TVI curves to CMYK data before ICC transform.
    /// Assumes input is CMYK 16-bit per channel (0-65535).
    /// </summary>
    public void ApplyTVI(byte[] input, byte[] output, int pixelCount)
    {
        if (input == null || output == null || input.Length != output.Length)
            throw new ArgumentException("Input and output arrays must be non-null and same length");
        
        if (_tviCurves.Count == 0)
        {
            // No TVI curves - just copy
            Buffer.BlockCopy(input, 0, output, 0, input.Length);
            return;
        }
        
        var channelCount = _channels;
        var totalValues = pixelCount * channelCount;
        
        // Apply TVI per channel (C, M, Y, K)
        var channelOrder = new[] { SeparationColor.ProcessCyan, SeparationColor.ProcessMagenta, SeparationColor.ProcessYellow, SeparationColor.ProcessBlack };
        
        for (var ch = 0; ch < channelCount; ch++)
        {
            var separation = ch < channelOrder.Length ? channelOrder[ch] : SeparationColor.SpotColor;
            var curve = _tviCurves.GetValueOrDefault(separation);
            
            if (curve == null || curve.Points.Count < 2)
            {
                // No curve for this channel - copy as-is
                for (var i = 0; i < pixelCount; i++)
                {
                    var srcIdx = i * _channels + ch;
                    var dstIdx = i * _channels + ch;
                    output[dstIdx * 2] = input[srcIdx * 2];
                    output[dstIdx * 2 + 1] = input[srcIdx * 2 + 1];
                }
                continue;
            }
            
            // Apply TVI curve to each pixel in this channel
            for (var i = 0; i < pixelCount; i++)
            {
                var srcIdx = i * _channels + ch;
                var dstIdx = i * _channels + ch;
                
                // Read 16-bit value (little-endian)
                var value = (ushort)(input[srcIdx * 2] | (input[srcIdx * 2 + 1] << 8));
                
                // Convert to 0-100% tone
                var tone = value / 65535.0 * 100.0;
                
                // Apply TVI curve
                var modifiedTone = curve.Apply(tone);
                
                // Convert back to 16-bit
                var newValue = (ushort)Math.Round(Math.Clamp(modifiedTone / 100.0 * 65535.0, 0, 65535));
                
                // Write back (little-endian)
                output[dstIdx * 2] = (byte)(newValue & 0xFF);
                output[dstIdx * 2 + 1] = (byte)((newValue >> 8) & 0xFF);
            }
        }
    }
}

/// <summary>
/// Ink limit enforcement stage
/// </summary>
public sealed class InkLimitStage
{
    private readonly double _inkLimitPercent;
    private readonly int _channels;
    
    public InkLimitStage(double inkLimitPercent, int channels = 4)
    {
        _inkLimitPercent = Math.Clamp(inkLimitPercent, 0, 400);
        _channels = channels;
    }
    
    /// <summary>
    /// Enforces ink limit on CMYK data.
    /// If total ink exceeds limit, scales all channels proportionally.
    /// </summary>
    public void EnforceLimit(byte[] data, int pixelCount)
    {
        if (data == null)
            throw new ArgumentNullException(nameof(data));
            
        if (_inkLimitPercent >= 400)
            return; // No limit
        
        var limit = _inkLimitPercent / 100.0;
        var channels = 4; // CMYK
        
        for (var i = 0; i < pixelCount; i++)
        {
            var baseIdx = i * 8; // 4 channels * 2 bytes each
            if (baseIdx + 7 >= data.Length)
                break;
            
            var cVal = (ushort)(data[baseIdx] | (data[baseIdx + 1] << 8));
            var mVal = (ushort)(data[baseIdx + 2] | (data[baseIdx + 3] << 8));
            var yVal = (ushort)(data[baseIdx + 4] | (data[baseIdx + 5] << 8));
            var kVal = (ushort)(data[baseIdx + 6] | (data[baseIdx + 7] << 8));
            
            var total = (cVal + mVal + yVal + kVal) / 65535.0;
            
            if (total > limit)
            {
                var scale = limit / total;
                var cVal2 = (ushort)Math.Round(cVal * scale);
                var mVal2 = (ushort)Math.Round(mVal * scale);
                var yVal2 = (ushort)Math.Round(yVal * scale);
                var kVal2 = (ushort)Math.Round(kVal * scale);
                
                data[baseIdx] = (byte)(cVal2 & 0xFF);
                data[baseIdx + 1] = (byte)((cVal2 >> 8) & 0xFF);
                data[baseIdx + 2] = (byte)(mVal2 & 0xFF);
                data[baseIdx + 3] = (byte)((mVal2 >> 8) & 0xFF);
                data[baseIdx + 4] = (byte)(yVal2 & 0xFF);
                data[baseIdx + 5] = (byte)((yVal2 >> 8) & 0xFF);
                data[baseIdx + 6] = (byte)(kVal2 & 0xFF);
                data[baseIdx + 7] = (byte)((kVal2 >> 8) & 0xFF);
            }
        }
    }
}