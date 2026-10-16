using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SoftProof.Core.Interfaces;
using SoftProof.Core.Models;
using CorePixelFormat = SoftProof.Core.Interfaces.PixelFormat;

namespace SoftProof.Viewer;

/// <summary>
/// High-performance raster viewer for 1-bit separation data
/// Supports zoom, pan, rotation, and real-time soft proofing
/// </summary>
public class SeparationViewer : FrameworkElement
{
    private WriteableBitmap? _bitmap;
    private byte[]? _sourcePixels;
    private int _sourceWidth;
    private int _sourceHeight;
    private int _sourceStride;
    private CorePixelFormat _sourceFormat;
    
    // View transform
    private Matrix _viewMatrix = Matrix.Identity;
    private Point _panStart;
    private bool _isPanning;
    
    // Separation visualization
    private SeparationColor _activeSeparation = SeparationColor.ProcessCyan;
    private Dictionary<SeparationColor, Color> _separationColors = new()
    {
        { SeparationColor.ProcessCyan, Colors.Cyan },
        { SeparationColor.ProcessMagenta, Colors.Magenta },
        { SeparationColor.ProcessYellow, Colors.Yellow },
        { SeparationColor.ProcessBlack, Colors.Black },
        { SeparationColor.Orange, Colors.Orange },
        { SeparationColor.Green, Colors.Green },
        { SeparationColor.Violet, Colors.Violet },
        { SeparationColor.Red, Colors.Red },
        { SeparationColor.Blue, Colors.Blue },
        { SeparationColor.White, Colors.White },
        { SeparationColor.Varnish, Colors.LightGray },
        { SeparationColor.Primer, Colors.Gray },
        { SeparationColor.SpotColor, Colors.HotPink }
    };
    
    // Soft proofing
    private IColorEngine? _colorEngine;
    private ProofTransform? _proofTransform;
    private bool _softProofingEnabled;
    
    public SeparationViewer()
    {
        Focusable = true;
        ClipToBounds = true;
    }
    
    /// <summary>
    /// Load 1-bit separation data
    /// </summary>
    public void LoadSeparation(byte[] pixels, int width, int height, int stride, SeparationColor color)
    {
        _sourcePixels = pixels;
        _sourceWidth = width;
        _sourceHeight = height;
        _sourceStride = stride;
        _sourceFormat = CorePixelFormat.Gray8; // 1-bit expanded to 8-bit
        _activeSeparation = color;
        
        CreateBitmap();
        InvalidateVisual();
    }
    
    /// <summary>
    /// Load composite CMYK separation set
    /// </summary>
    public void LoadComposite(Dictionary<SeparationColor, byte[]> separations, int width, int height, int stride)
    {
        _sourceWidth = width;
        _sourceHeight = height;
        _sourceStride = stride;
        
        // Composite separations into CMYK
        var composite = new byte[width * height * 4]; // CMYK
        var colors = new[] { SeparationColor.ProcessCyan, SeparationColor.ProcessMagenta, 
                            SeparationColor.ProcessYellow, SeparationColor.ProcessBlack };
        
        for (int c = 0; c < 4; c++)
        {
            if (separations.TryGetValue(colors[c], out var sepData))
            {
                // Expand 1-bit to 8-bit and invert (1-bit: 1=ink, 0=paper)
                for (int y = 0; y < height; y++)
                {
                    var srcOffset = y * stride;
                    var dstOffset = y * width * 4 + c;
                    for (int x = 0; x < width; x++)
                    {
                        // 1-bit unpacking
                        var byteIndex = x / 8;
                        var bitIndex = 7 - (x % 8);
                        var ink = (sepData[srcOffset + byteIndex] >> bitIndex) & 1;
                        composite[dstOffset + x * 4] = (byte)(ink * 255);
                    }
                }
            }
        }
        
        _sourcePixels = composite;
        _sourceFormat = CorePixelFormat.CMYK32;
        _sourceStride = width * 4;
        
        CreateBitmap();
        InvalidateVisual();
    }
    
    private void CreateBitmap()
    {
        if (_sourcePixels == null) return;
        
        var dpiX = 96.0;
        var dpiY = 96.0;
        
        var wpfFormat = _sourceFormat switch
        {
            CorePixelFormat.Gray8 => System.Windows.Media.PixelFormats.Gray8,
            CorePixelFormat.CMYK32 => System.Windows.Media.PixelFormats.Cmyk32,
            _ => System.Windows.Media.PixelFormats.Bgra32
        };
        
        _bitmap = new WriteableBitmap(_sourceWidth, _sourceHeight, dpiX, dpiY, wpfFormat, null);
        _bitmap.WritePixels(
            new Int32Rect(0, 0, _sourceWidth, _sourceHeight),
            _sourcePixels, _sourceStride, 0);
    }
    
    /// <summary>
    /// Enable/disable soft proofing
    /// </summary>
    public void SetSoftProofing(IColorEngine colorEngine, ProofTransform proofTransform, bool enabled)
    {
        _colorEngine = colorEngine;
        _proofTransform = proofTransform;
        _softProofingEnabled = enabled;
        
        if (enabled && _sourcePixels != null)
        {
            ApplySoftProofing();
        }
        else
        {
            CreateBitmap(); // Revert to original
        }
        InvalidateVisual();
    }
    
    private async void ApplySoftProofing()
    {
        if (_colorEngine == null || _proofTransform == null || _sourcePixels == null) return;
        
        try
        {
            var sourceBitmap = new BitmapData
            {
                Width = _sourceWidth,
                Height = _sourceHeight,
                Stride = _sourceStride,
                Format = _sourceFormat,
                Pixels = _sourcePixels
            };
            
            // Use simulation transform if available
            var transform = _proofTransform.SimulationTransform ?? _proofTransform.DisplayTransform;
            if (transform != null)
            {
                var result = await _colorEngine.ApplyTransformAsync(transform, sourceBitmap);
                
                // Update bitmap with proofed result
                if (_bitmap != null)
                {
                    _bitmap.WritePixels(
                        new Int32Rect(0, 0, result.Width, result.Height),
                        result.Pixels, result.Stride, 0);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Soft proofing failed: {ex.Message}");
        }
    }
    
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        
        if (_bitmap != null)
        {
            dc.PushTransform(new MatrixTransform(_viewMatrix));
            dc.DrawImage(_bitmap, new Rect(0, 0, _sourceWidth, _sourceHeight));
            dc.Pop();
        }
        
        // Draw ruler/guides
        DrawRulers(dc);
    }
    
    private void DrawRulers(DrawingContext dc)
    {
        if (_sourceWidth == 0) return;
        
        var pen = new Pen(Brushes.LightGray, 1 / _viewMatrix.M11);
        var fontSize = 10 / _viewMatrix.M11;
        var typeface = new Typeface("Consolas");
        
        // Horizontal ruler (top)
        for (int x = 0; x <= _sourceWidth; x += 100)
        {
            var pt = _viewMatrix.Transform(new Point(x, 0));
            dc.DrawLine(pen, pt, new Point(pt.X, pt.Y + 10 / _viewMatrix.M11));
            
            var text = new FormattedText($"{x}px", System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, typeface, fontSize, Brushes.DarkGray);
            dc.DrawText(text, new Point(pt.X - text.Width / 2, pt.Y + 12 / _viewMatrix.M11));
        }
        
        // Vertical ruler (left)
        for (int y = 0; y <= _sourceHeight; y += 100)
        {
            var pt = _viewMatrix.Transform(new Point(0, y));
            dc.DrawLine(pen, pt, new Point(pt.X + 10 / _viewMatrix.M11, pt.Y));
            
            var text = new FormattedText($"{y}px", System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, typeface, fontSize, Brushes.DarkGray);
            dc.DrawText(text, new Point(pt.X + 12 / _viewMatrix.M11, pt.Y - text.Height / 2));
        }
    }
    
    // Zoom/Pan handling
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        
        var zoomFactor = e.Delta > 0 ? 1.1 : 1 / 1.1;
        var mousePos = e.GetPosition(this);
        var invertedMatrix = _viewMatrix;
        invertedMatrix.Invert();
        var logicalPos = invertedMatrix.Transform(mousePos);
        
        _viewMatrix.ScaleAt(zoomFactor, zoomFactor, logicalPos.X, logicalPos.Y);
        InvalidateVisual();
    }
    
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        
        if (e.MiddleButton == MouseButtonState.Pressed || 
            (e.LeftButton == MouseButtonState.Pressed && Keyboard.IsKeyDown(Key.Space)))
        {
            _isPanning = true;
            _panStart = e.GetPosition(this);
            CaptureMouse();
        }
        
        Focus();
    }
    
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        
        if (_isPanning)
        {
            var current = e.GetPosition(this);
            var delta = current - _panStart;
            _viewMatrix.Translate(delta.X / _viewMatrix.M11, delta.Y / _viewMatrix.M22);
            _panStart = current;
            InvalidateVisual();
        }
    }
    
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        _isPanning = false;
        ReleaseMouseCapture();
    }
    
    /// <summary>
    /// Reset view to fit
    /// </summary>
    public void FitToView()
    {
        if (_sourceWidth == 0 || ActualWidth == 0 || ActualHeight == 0) return;
        
        var scaleX = ActualWidth / _sourceWidth;
        var scaleY = ActualHeight / _sourceHeight;
        var scale = Math.Min(scaleX, scaleY) * 0.95; // 5% margin
        
        _viewMatrix = Matrix.Identity;
        _viewMatrix.Scale(scale, scale);
        _viewMatrix.Translate(
            (ActualWidth - _sourceWidth * scale) / 2,
            (ActualHeight - _sourceHeight * scale) / 2);
        
        InvalidateVisual();
    }
    
    /// <summary>
    /// Get color at screen position (for measurement tools)
    /// </summary>
    public Color GetColorAt(Point screenPoint)
    {
        if (_bitmap == null || _sourcePixels == null) return Colors.Transparent;
        
        var invertedMatrix = _viewMatrix;
        invertedMatrix.Invert();
        var logicalPoint = invertedMatrix.Transform(screenPoint);
        var x = (int)Math.Floor(logicalPoint.X);
        var y = (int)Math.Floor(logicalPoint.Y);
        
        if (x < 0 || x >= _sourceWidth || y < 0 || y >= _sourceHeight)
            return Colors.Transparent;
        
        if (_sourceFormat == CorePixelFormat.CMYK32)
        {
            var idx = (y * _sourceStride) + x * 4;
            return Color.FromArgb(255, _sourcePixels[idx], _sourcePixels[idx + 1], _sourcePixels[idx + 2]);
        }
        
        var grayIdx = y * _sourceStride + x;
        var gray = _sourcePixels[grayIdx];
        var sepColor = _separationColors[_activeSeparation];
        return Color.FromArgb(255, 
            (byte)(sepColor.R * gray / 255),
            (byte)(sepColor.G * gray / 255),
            (byte)(sepColor.B * gray / 255));
    }
    
    /// <summary>
    /// Get raw separation value at point (0-1)
    /// </summary>
    public float GetSeparationValueAt(Point screenPoint)
    {
        if (_sourcePixels == null) return 0;
        
        var invertedMatrix = _viewMatrix;
        invertedMatrix.Invert();
        var logicalPoint = invertedMatrix.Transform(screenPoint);
        var x = (int)Math.Floor(logicalPoint.X);
        var y = (int)Math.Floor(logicalPoint.Y);
        
        if (x < 0 || x >= _sourceWidth || y < 0 || y >= _sourceHeight)
            return 0;
        
        if (_sourceFormat == CorePixelFormat.Gray8)
        {
            var idx = y * _sourceStride + x;
            return _sourcePixels[idx] / 255f;
        }
        
        return 0;
    }
}