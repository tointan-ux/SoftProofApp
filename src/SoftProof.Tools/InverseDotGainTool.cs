using System.Numerics;
using MathNet.Numerics;
using MathNet.Numerics.IntegralTransforms;
using Microsoft.Extensions.Logging;
using SoftProof.Core.Models;

namespace SoftProof.Tools;

/// <summary>
/// Inverse Dot Gain (IDG) Tool - Hamillroad's patented technology
/// Simulates how stochastic/FM screening reproduces on press by modeling
/// the optical dot gain from light scattering in paper/substrate
/// </summary>
public class InverseDotGainTool
{
    private readonly ILogger<InverseDotGainTool> _logger;
    
    public InverseDotGainTool(ILogger<InverseDotGainTool> logger)
    {
        _logger = logger;
    }
    
    /// <summary>
    /// Apply IDG simulation to 1-bit stochastic screening data
    /// </summary>
    public byte[] ApplyIdgSimulation(
        byte[] separationData,
        int width,
        int height,
        IdgParameters parameters)
    {
        _logger.LogInformation("Applying IDG simulation: {Width}x{Height}, ScreenFreq={Freq}LPI, Type={Type}",
            width, height, parameters.ScreenFrequency, parameters.ScreeningType);
        
        // Convert 1-bit to float (0-1)
        var floatData = new float[width * height];
        for (int i = 0; i < separationData.Length; i++)
        {
            for (int b = 0; b < 8; b++)
            {
                var idx = i * 8 + b;
                if (idx < floatData.Length)
                {
                    floatData[idx] = (separationData[i] >> (7 - b)) & 1;
                }
            }
        }
        
        // Apply point spread function (PSF) based on screening type
        var psf = GeneratePointSpreadFunction(parameters);
        var convolved = Convolve2D(floatData, width, height, psf);
        
        // Apply tone reproduction curve (TRC) for paper/ink interaction
        var trc = GenerateTrc(parameters);
        for (int i = 0; i < convolved.Length; i++)
        {
            convolved[i] = ApplyTrc(convolved[i], trc);
        }
        
        // Convert back to 1-bit with thresholding (simulating press threshold)
        var result = new byte[(width * height + 7) / 8];
        var threshold = parameters.PressThreshold;
        
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var idx = y * width + x;
                var bitIdx = idx / 8;
                var bitPos = 7 - (idx % 8);
                
                if (convolved[idx] > threshold)
                {
                    result[bitIdx] |= (byte)(1 << bitPos);
                }
            }
        }
        
        return result;
    }
    
    /// <summary>
    /// Generate Point Spread Function for different screening types
    /// </summary>
    private float[,] GeneratePointSpreadFunction(IdgParameters parameters)
    {
        // PSF radius based on screen frequency and paper scatter
        var radiusMm = parameters.OpticalDotGainMm; // Typical 0.02-0.05mm for coated, 0.08-0.15 for uncoated
        var pixelsPerMm = parameters.Resolution / 25.4f;
        var radiusPx = Math.Max(1, (int)Math.Round(radiusMm * pixelsPerMm));
        var size = radiusPx * 2 + 1;
        
        var psf = new float[size, size];
        var center = radiusPx;
        
        switch (parameters.ScreeningType)
        {
            case ScreeningType.FM:
            case ScreeningType.HDS:
                // Stochastic: Gaussian-like scatter with stronger center
                GenerateGaussianPsf(psf, center, radiusPx, parameters.PaperScatterFactor);
                break;
                
            case ScreeningType.AM:
                // Conventional: structured, directional scatter
                GenerateAmPsf(psf, center, radiusPx, parameters.ScreenAngle);
                break;
                
            case ScreeningType.Hybrid:
                // Hybrid: combination
                var gaussian = new float[size, size];
                var am = new float[size, size];
                GenerateGaussianPsf(gaussian, center, radiusPx, parameters.PaperScatterFactor);
                GenerateAmPsf(am, center, radiusPx, parameters.ScreenAngle);
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                        psf[y, x] = (gaussian[y, x] + am[y, x]) * 0.5f;
                break;
                
            case ScreeningType.Crystal:
                // Crystal: ordered FM, sharper PSF
                GenerateGaussianPsf(psf, center, radiusPx, parameters.PaperScatterFactor * 0.7f);
                break;
                
            case ScreeningType.Sublima:
                // Sublima: specialized stochastic
                GenerateGaussianPsf(psf, center, radiusPx, parameters.PaperScatterFactor * 0.85f);
                break;
        }
        
        // Normalize
        var sum = 0f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                sum += psf[y, x];
        
        if (sum > 0)
        {
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    psf[y, x] /= sum;
        }
        
        return psf;
    }
    
    private void GenerateGaussianPsf(float[,] psf, int center, int radius, float scatterFactor)
    {
        var sigma = radius * scatterFactor;
        var twoSigmaSq = 2 * sigma * sigma;
        
        for (int y = 0; y < psf.GetLength(0); y++)
        {
            for (int x = 0; x < psf.GetLength(1); x++)
            {
                var dx = x - center;
                var dy = y - center;
                var distSq = dx * dx + dy * dy;
                psf[y, x] = MathF.Exp(-distSq / twoSigmaSq);
            }
        }
    }
    
    private void GenerateAmPsf(float[,] psf, int center, int radius, float screenAngle)
    {
        // AM screening has directional scatter along screen angle
        var angleRad = screenAngle * MathF.PI / 180f;
        var cosA = MathF.Cos(angleRad);
        var sinA = MathF.Sin(angleRad);
        
        for (int y = 0; y < psf.GetLength(0); y++)
        {
            for (int x = 0; x < psf.GetLength(1); x++)
            {
                var dx = x - center;
                var dy = y - center;
                
                // Rotate to screen angle coordinates
                var u = dx * cosA + dy * sinA;
                var v = -dx * sinA + dy * cosA;
                
                // Elongated along screen angle
                var distSq = (u * u) / 1.5f + (v * v) * 1.5f;
                var sigma = radius * 0.3f;
                psf[y, x] = MathF.Exp(-distSq / (2 * sigma * sigma));
            }
        }
    }
    
    /// <summary>
    /// 2D Convolution using FFT for performance
    /// </summary>
    private float[] Convolve2D(float[] input, int width, int height, float[,] kernel)
    {
        var kSize = kernel.GetLength(0);
        var kRadius = kSize / 2;
        
        // For small kernels, direct convolution is faster
        if (kSize <= 15)
        {
            return ConvolveDirect(input, width, height, kernel);
        }
        
        // FFT-based convolution for large kernels
        return ConvolveFft(input, width, height, kernel);
    }
    
    private float[] ConvolveDirect(float[] input, int width, int height, float[,] kernel)
    {
        var kSize = kernel.GetLength(0);
        var kRadius = kSize / 2;
        var output = new float[width * height];
        
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float sum = 0;
                
                for (int ky = 0; ky < kSize; ky++)
                {
                    var iy = y + ky - kRadius;
                    if (iy < 0 || iy >= height) continue;
                    
                    for (int kx = 0; kx < kSize; kx++)
                    {
                        var ix = x + kx - kRadius;
                        if (ix < 0 || ix >= width) continue;
                        
                        sum += input[iy * width + ix] * kernel[ky, kx];
                    }
                }
                
                output[y * width + x] = sum;
            }
        }
        
        return output;
    }
    
    private float[] ConvolveFft(float[] input, int width, int height, float[,] kernel)
    {
        // Pad to power of 2
        var fftWidth = 1;
        while (fftWidth < width + kernel.GetLength(0)) fftWidth <<= 1;
        var fftHeight = 1;
        while (fftHeight < height + kernel.GetLength(1)) fftHeight <<= 1;
        
        // Prepare complex arrays
        var inputComplex = new Complex[fftWidth * fftHeight];
        var kernelComplex = new Complex[fftWidth * fftHeight];
        
        // Copy input
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                inputComplex[y * fftWidth + x] = input[y * width + x];
            }
        }
        
        // Copy kernel (centered)
        var kSize = kernel.GetLength(0);
        var kRadius = kSize / 2;
        for (int y = 0; y < kSize; y++)
        {
            for (int x = 0; x < kSize; x++)
            {
                var fx = (x - kRadius + fftWidth) % fftWidth;
                var fy = (y - kRadius + fftHeight) % fftHeight;
                kernelComplex[fy * fftWidth + fx] = kernel[y, x];
            }
        }
        
        // FFT both
        Fourier.Forward(inputComplex, FourierOptions.Matlab);
        Fourier.Forward(kernelComplex, FourierOptions.Matlab);
        
        // Multiply
        for (int i = 0; i < inputComplex.Length; i++)
        {
            inputComplex[i] *= kernelComplex[i];
        }
        
        // Inverse FFT
        Fourier.Inverse(inputComplex, FourierOptions.Matlab);
        
        // Extract real part
        var output = new float[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                output[y * width + x] = (float)inputComplex[y * fftWidth + x].Real;
            }
        }
        
        return output;
    }
    
    /// <summary>
    /// Generate Tone Reproduction Curve for paper/ink simulation
    /// </summary>
    private float[] GenerateTrc(IdgParameters parameters)
    {
        // TRC models: input tint % -> printed dot %
        // Accounts for mechanical dot gain + optical dot gain
        var trc = new float[256];
        
        for (int i = 0; i < 256; i++)
        {
            var tint = i / 255f;
            
            // Murray-Davies effective dot area
            var mechanicalGain = parameters.MechanicalDotGain / 100f;
            var opticalGain = parameters.OpticalDotGain / 100f;
            
            // Yule-Nielsen n-factor for optical gain
            var n = parameters.YuleNielsenN; // Typically 1.7-2.2
            
            var effectiveDot = MathF.Pow(tint, 1 + mechanicalGain);
            var printed = MathF.Pow(effectiveDot, 1 / n) * (1 + opticalGain);
            
            trc[i] = Math.Clamp(printed, 0, 1);
        }
        
        return trc;
    }
    
    private float ApplyTrc(float value, float[] trc)
    {
        var idx = (int)Math.Clamp(value * 255, 0, 255);
        return trc[idx];
    }
    
    /// <summary>
    /// Analyze stochastic screening quality
    /// </summary>
    public StochasticAnalysis AnalyzeStochasticQuality(byte[] separationData, int width, int height)
    {
        // Convert to float
        var floatData = new float[width * height];
        for (int i = 0; i < separationData.Length; i++)
        {
            for (int b = 0; b < 8; b++)
            {
                var idx = i * 8 + b;
                if (idx < floatData.Length)
                    floatData[idx] = (separationData[i] >> (7 - b)) & 1;
            }
        }
        
        // Calculate metrics
        var analysis = new StochasticAnalysis();
        
        // 1. Dot density / coverage
        analysis.Coverage = floatData.Average();
        
        // 2. Granularity (RMS variation)
        var mean = analysis.Coverage;
        var variance = floatData.Select(v => (v - mean) * (v - mean)).Average();
        analysis.Granularity = MathF.Sqrt(variance);
        
        // 3. Power spectrum analysis (frequency domain)
        var spectrum = ComputePowerSpectrum(floatData, width, height);
        analysis.PeakFrequency = FindPeakFrequency(spectrum, width, height);
        analysis.SpectralUniformity = ComputeSpectralUniformity(spectrum);
        
        // 4. Pair correlation function (clustering)
        analysis.ClusteringIndex = ComputePairCorrelation(floatData, width, height);
        
        // 5. Visual quality score (0-100)
        analysis.QualityScore = ComputeQualityScore(analysis);
        
        return analysis;
    }
    
    private float[] ComputePowerSpectrum(float[] data, int width, int height)
    {
        var complex = new Complex[width * height];
        for (int i = 0; i < data.Length; i++)
            complex[i] = data[i];
        
        Fourier.Forward(complex, FourierOptions.Matlab);
        
        var spectrum = new float[width * height];
        for (int i = 0; i < complex.Length; i++)
            spectrum[i] = (float)(complex[i].Real * complex[i].Real + complex[i].Imaginary * complex[i].Imaginary);
        
        return spectrum;
    }
    
    private float FindPeakFrequency(float[] spectrum, int width, int height)
    {
        // Find dominant frequency (excluding DC)
        float maxVal = 0;
        int maxIdx = 0;
        
        for (int y = 1; y < height / 2; y++)
        {
            for (int x = 1; x < width / 2; x++)
            {
                var idx = y * width + x;
                if (spectrum[idx] > maxVal)
                {
                    maxVal = spectrum[idx];
                    maxIdx = idx;
                }
            }
        }
        
        // Convert to cycles per mm
        return maxIdx > 0 ? maxIdx / (float)Math.Max(width, height) : 0;
    }
    
    private float ComputeSpectralUniformity(float[] spectrum)
    {
        // Measure how flat the spectrum is (good stochastic = flat)
        var values = spectrum.Where(v => v > 0).ToArray();
        if (values.Length == 0) return 0;
        
        var mean = values.Average();
        var variance = values.Select(v => (v - mean) * (v - mean)).Average();
        var stdDev = MathF.Sqrt(variance);
        
        return 1f - Math.Clamp(stdDev / mean, 0, 1);
    }
    
    private float ComputePairCorrelation(float[] data, int width, int height)
    {
        // Simplified: measure nearest neighbor clustering
        var clustered = 0;
        var total = 0;
        
        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                var idx = y * width + x;
                if (data[idx] > 0.5f)
                {
                    total++;
                    // Check 4-neighbors
                    var neighbors = new[]
                    {
                        data[(y-1)*width + x],
                        data[(y+1)*width + x],
                        data[y*width + x-1],
                        data[y*width + x+1]
                    };
                    
                    if (neighbors.Count(n => n > 0.5f) >= 2)
                        clustered++;
                }
            }
        }
        
        return total > 0 ? clustered / (float)total : 0;
    }
    
    private float ComputeQualityScore(StochasticAnalysis analysis)
    {
        // Weighted scoring (0-100, higher = better stochastic quality).
        //
        // The old formula subtracted Granularity * 200, but Granularity is the
        // RMS deviation of 0/1 pixel data. For a 50% screen that is ~0.5, so
        // 0.5 * 200 = 100 — which zeroed the score for every realistic screen.
        //
        // Granularity for a good FM screen at 50% coverage is still ~0.5
        // (binary pixels), so we normalise it against the theoretical max
        // (0.5 for a two-value distribution) and scale gently.
        
        var score = 100f;
        
        // Granularity: normalise to [0,1] range (max RMS for binary data is 0.5).
        // Lower granularity relative to the theoretical max = better.
        var granularityPenalty = Math.Clamp(analysis.Granularity / 0.5f, 0, 1) * 30f;
        score -= granularityPenalty;
        
        // Spectral uniformity: [0,1], more uniform spectrum = better stochastic.
        score += analysis.SpectralUniformity * 30f;
        
        // Clustering: [0,1], lower clustering = better stochastic distribution.
        // Ideal FM has clustering index near 0 (no neighbor preference).
        score -= analysis.ClusteringIndex * 40f;
        
        return Math.Clamp(score, 0, 100);
    }
}

/// <summary>
/// IDG Simulation Parameters
/// </summary>
public class IdgParameters
{
    public float Resolution { get; set; } = 2400; // DPI
    public float ScreenFrequency { get; set; } = 175; // LPI
    public ScreeningType ScreeningType { get; set; } = ScreeningType.FM;
    public float ScreenAngle { get; set; } = 45; // degrees
    
    // Paper/press parameters
    public float OpticalDotGainMm { get; set; } = 0.03f; // mm
    public float OpticalDotGain { get; set; } = 15; // %
    public float MechanicalDotGain { get; set; } = 10; // %
    public float PaperScatterFactor { get; set; } = 1.0f;
    public float YuleNielsenN { get; set; } = 2.0f;
    public float PressThreshold { get; set; } = 0.5f;
    
    // Ink/paper
    public string PaperType { get; set; } = "Coated"; // Coated, Uncoated, Newsprint
    public string InkSet { get; set; } = "ISO2846-1"; // ISO, SWOP, GRACoL
}

/// <summary>
/// Stochastic screening analysis results
/// </summary>
public class StochasticAnalysis
{
    public float Coverage { get; set; }
    public float Granularity { get; set; }
    public float PeakFrequency { get; set; }
    public float SpectralUniformity { get; set; }
    public float ClusteringIndex { get; set; }
    public float QualityScore { get; set; }
    public DateTime AnalyzedAt { get; set; } = DateTime.UtcNow;
}