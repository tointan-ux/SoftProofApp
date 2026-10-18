using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SoftProof.RIPParsers.Raster;
using Xunit;

namespace SoftProof.Tests.Analysis;

/// <summary>
/// Reference loading: a rasterised page is resized to the plate grid and thresholded into an
/// ink mask, so the comparer can work in plate pixels.
/// </summary>
public class ReferenceRasterTests
{
    [Fact]
    public void LoadsImage_ResizesToPlate_AndThresholdsInk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"spref-{Guid.NewGuid():N}.png");

        try
        {
            using (var image = new Image<Rgba32>(40, 20, new Rgba32(255, 255, 255)))
            {
                for (var y = 5; y < 15; y++)
                    for (var x = 5; x < 15; x++)
                        image[x, y] = new Rgba32(0, 0, 0);

                image.SaveAsPng(path);
            }

            var mask = ReferenceRaster.LoadInkMask(path, 80, 40);

            Assert.Equal(80 * 40, mask.Length);
            Assert.Equal(0, mask[0]);                    // corner is paper
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
    public void MissingFile_ThrowsClearError()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"spref-missing-{Guid.NewGuid():N}.png");
        Assert.Throws<FileNotFoundException>(() => ReferenceRaster.LoadInkMask(missing, 10, 10));
    }
}
