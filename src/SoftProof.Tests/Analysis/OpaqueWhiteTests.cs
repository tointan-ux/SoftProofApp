using SoftProof.Core.Models;
using Xunit;

namespace SoftProof.Tests.Analysis;

/// <summary>
/// White ink must composite opaquely — it paints white over the substrate instead of tinting it.
/// A plate can qualify either because the RIP named it white, or because the user ticked
/// "opaque white" in the custom colour dialog.
/// </summary>
public class OpaqueWhiteTests
{
    private static PlateLayer Plate(string name, SeparationColor color, bool opaqueWhite = false) =>
        new(new Separation { Name = name, Color = color }, 4, 4, new byte[16], 200, 180, 40)
        {
            OpaqueWhite = opaqueWhite
        };

    [Fact]
    public void ExplicitOption_MarksThePlateAsOpaqueWhite()
    {
        var plate = Plate("Spot 1", SeparationColor.SpotColor, opaqueWhite: true);

        Assert.True(plate.PaintsOpaqueWhite);
    }

    [Fact]
    public void RipWhiteSeparation_IsOpaqueWhiteWithoutTheOption()
    {
        var plate = Plate("White", SeparationColor.White);

        Assert.True(plate.PaintsOpaqueWhite);
    }

    [Fact]
    public void PlateNamedWhite_IsOpaqueWhiteWithoutTheOption()
    {
        var plate = Plate("Opaque White Ink", SeparationColor.SpotColor);

        Assert.True(plate.PaintsOpaqueWhite);
    }

    [Fact]
    public void OrdinaryInk_IsNotOpaqueWhite()
    {
        var plate = Plate("Cyan", SeparationColor.ProcessCyan);

        Assert.False(plate.PaintsOpaqueWhite);
    }

    [Fact]
    public void OptionCanBeClearedAgain()
    {
        var plate = Plate("Cyan", SeparationColor.ProcessCyan, opaqueWhite: true);
        Assert.True(plate.PaintsOpaqueWhite);

        plate.OpaqueWhite = false;

        Assert.False(plate.PaintsOpaqueWhite);
    }
}
