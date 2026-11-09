using System.Drawing;
using System.Drawing.Imaging;
using SoftProof.RIPParsers.Raster;
using Xunit;

namespace SoftProof.Tests.Analysis;

/// <summary>
/// Reference loading: a rasterised page is resized to the plate grid and thresholded into an
/// ink mask, so the comparer can work in plate pixels.
/// </summary>
public class ReferenceRasterTests
{
    private static void WriteReferencePng(string path, int width, int height)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);

        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.White);
            using var ink = new SolidBrush(Color.Black);
            graphics.FillRectangle(ink, 5, 5, 10, 10);
        }

        bitmap.Save(path, ImageFormat.Png);
    }

    [Fact]
    public void LoadsImage_ResizesToPlate_AndThresholdsInk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"spref-{Guid.NewGuid():N}.png");

        try
        {
            WriteReferencePng(path, 40, 20);

            var mask = ReferenceRaster.LoadInkMask(path, 80, 40);

            Assert.Equal(80 * 40, mask.Length);
            Assert.Equal(0, mask[0]);                     // corner is paper
            Assert.Equal(1, mask[20 * 80 + 20]);          // inside the scaled black square is ink

            var ink = mask.Count(v => v != 0);
            Assert.InRange(ink, 350, 450);                // 20x20 scaled block, within rounding
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void OversizedPlate_IsRejectedBeforeResizing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"spref-{Guid.NewGuid():N}.png");

        try
        {
            WriteReferencePng(path, 40, 20);

            var ex = Assert.Throws<InvalidOperationException>(
                () => ReferenceRaster.LoadInkMask(path, 100_000, 100_000));

            Assert.Contains("limited to", ex.Message);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void MissingFile_ThrowsClearError()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"spref-missing-{Guid.NewGuid():N}.png");
        Assert.Throws<FileNotFoundException>(() => ReferenceRaster.LoadInkMask(missing, 10, 10));
    }
}
