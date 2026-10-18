using SoftProof.Core.Models;
using SoftProof.RIPParsers.Raster;
using SoftProof.RIPParsers.Raster.Pyramid;
using Xunit;

namespace SoftProof.Tests.Raster;

public class PyramidCacheTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private sealed class Collector : IProgress<PlateLoadProgress>
    {
        public List<PlateLoadProgress> Items { get; } = new();
        public void Report(PlateLoadProgress value) => Items.Add(value);
    }

    [Fact]
    public void Pyramid_HalvesLevelsWithAreaAveraging()
    {
        // 128x128: left half paper, right half solid.
        var coverage = new byte[128 * 128];
        for (var y = 0; y < 128; y++)
            for (var x = 0; x < 128; x++)
                coverage[y * 128 + x] = (byte)(x < 64 ? 0 : 255);

        var pyramid = PlatePyramid.FromCoverage(coverage, 128, 128, factor: 1);

        Assert.True(pyramid.Levels.Count >= 2);
        var level = pyramid.Base;
        Assert.Equal(128, level.Width);
        Assert.Equal(50.0, level.InkedPercent(), precision: 1);

        var half = pyramid.Levels[1];
        Assert.Equal(64, half.Width);
        Assert.Equal(64, half.Height);
        Assert.Equal(2, half.Factor);
        Assert.Equal(50.0, half.InkedPercent(), precision: 1);

        var coarse = pyramid.LevelFor(0.5);
        Assert.Equal(64, coarse.Width);
    }

    [Theory]
    [InlineData(1.0, 0)]
    [InlineData(0.75, 0)]
    [InlineData(0.5, 1)]
    [InlineData(0.25, 2)]
    [InlineData(0.125, 3)]
    [InlineData(0.01, 4)]
    public void IndexForZoom_SelectsTheCoarsestLevelThatStillCoversAScreenPixel(
        double zoom, int expected)
    {
        // 1024 -> 512 -> 256 -> 128 -> 64: five levels.
        var coverage = new byte[1024 * 1024];
        var pyramid = PlatePyramid.FromCoverage(coverage, 1024, 1024, factor: 1);

        Assert.Equal(5, pyramid.Levels.Count);
        Assert.Equal(expected, pyramid.IndexForZoom(zoom));
        Assert.Equal(1 << expected, pyramid.Levels[expected].Factor);
    }

    [Fact]
    public void Loader_AttachesPyramidToThePlateLayer()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"softproof-cache-{Guid.NewGuid():N}");
        try
        {
            var loader = new PlateLoader(new PyramidCache(dir)) { MaxPixelsPerPlate = 2_000_000 };
            var plates = loader.LoadAsync(LargePlateJob()).GetAwaiter().GetResult();

            Assert.Single(plates);
            var pyramid = plates[0].Pyramid;
            Assert.NotNull(pyramid);
            Assert.True(pyramid!.Levels.Count > 1, "a decimated plate should have coarse levels");
            Assert.Same(pyramid.Base.Coverage, plates[0].Coverage);
            Assert.Equal(pyramid.Base.Factor * 2, pyramid.Levels[1].Factor);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Cache_RoundTripsEveryLevel()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"softproof-cache-{Guid.NewGuid():N}");
        try
        {
            var cache = new PyramidCache(dir);
            var coverage = new byte[64 * 64];
            for (var i = 0; i < coverage.Length; i++)
                coverage[i] = (byte)(i % 256);

            var pyramid = PlatePyramid.FromCoverage(coverage, 64, 64, factor: 4);
            var source = Fixture("large_bilevel_lzw.tif");

            cache.Put(source, 0, 2_000_000, pyramid);
            Assert.True(File.Exists(cache.PathForKey(
                PyramidCache.ComputeKey(source, 0, 2_000_000))));

            Assert.True(cache.TryGet(source, 0, 2_000_000, out var restored));
            Assert.NotNull(restored);
            Assert.Equal(pyramid.Levels.Count, restored!.Levels.Count);

            for (var i = 0; i < pyramid.Levels.Count; i++)
            {
                Assert.Equal(pyramid.Levels[i].Width, restored.Levels[i].Width);
                Assert.Equal(pyramid.Levels[i].Height, restored.Levels[i].Height);
                Assert.Equal(pyramid.Levels[i].Coverage, restored.Levels[i].Coverage);
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Cache_IgnoresCorruptEntry()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"softproof-cache-{Guid.NewGuid():N}");
        try
        {
            var cache = new PyramidCache(dir);
            var source = Fixture("large_bilevel_lzw.tif");
            var path = cache.PathForKey(PyramidCache.ComputeKey(source, 0, 2_000_000));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "not a pyramid");

            Assert.False(cache.TryGet(source, 0, 2_000_000, out _));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Loader_SecondLoadIsServedFromCache()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"softproof-cache-{Guid.NewGuid():N}");
        try
        {
            var cache = new PyramidCache(dir);
            var loader = new PlateLoader(cache) { MaxPixelsPerPlate = 2_000_000 };
            var job = LargePlateJob();

            var first = new Collector();
            var plates1 = await loader.LoadAsync(job, null, first);
            Assert.Single(plates1);
            Assert.Contains(first.Items, p => p.Stage == "decoding" && p.Percent > 0);
            Assert.Equal(100, first.Items[^1].Percent);
            Assert.DoesNotContain(first.Items, p => p.FromCache);

            // Second load: same file, same budget -> cache hit, no decode.
            var job2 = LargePlateJob();
            var second = new Collector();
            var plates2 = await loader.LoadAsync(job2, null, second);

            Assert.Single(plates2);
            Assert.Contains(second.Items, p => p.Stage == "cached" && p.FromCache);
            Assert.Equal(plates1[0].Coverage, plates2[0].Coverage);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Loader_HonoursCancellation()
    {
        var loader = new PlateLoader(new PyramidCache(
            Path.Combine(Path.GetTempPath(), $"softproof-cache-{Guid.NewGuid():N}")))
        {
            MaxPixelsPerPlate = 2_000_000
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => loader.LoadAsync(LargePlateJob(), null, null, cts.Token));
    }

    private static PrintJob LargePlateJob()
    {
        var job = new PrintJob
        {
            Name = "large",
            Format = RipFormat.OneBitTiffSeparations,
            Metadata = new JobMetadata { Width = 6000, Height = 4000, Resolution = 2400, BitDepth = 1 }
        };

        job.Separations.Add(new Separation
        {
            Name = "Cyan",
            Index = 0,
            FilePath = Fixture("large_bilevel_lzw.tif"),
            Color = SeparationColor.ProcessCyan
        });

        return job;
    }
}
