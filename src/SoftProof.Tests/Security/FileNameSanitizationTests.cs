using FluentAssertions;
using SoftProof.Core.Utilities;
using Xunit;

namespace SoftProof.Tests.Security;

/// <summary>
/// The sanitizer guards report file names. Replacing invalid characters is not enough on Windows:
/// reserved device names, trailing dots/spaces and over-long names all fail at write time — after
/// the user has already picked the folder.
/// </summary>
public class FileNameSanitizationTests
{
    [Fact]
    public void Invalid_path_characters_are_replaced()
    {
        SafeFileName.For(@"Cyan:/Plate*1?<>|""").Should().Be("Cyan__Plate_1_____")
            .And.NotContain(":").And.NotContain("*").And.NotContain("?");
    }

    [Fact]
    public void Path_traversal_cannot_survive()
    {
        var name = SafeFileName.For(@"..\..\Windows\System32\evil");

        name.Should().NotContain(@"\").And.NotContain("/");
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    public void Reserved_device_names_are_prefixed(string name)
    {
        SafeFileName.For(name).Should().StartWith("_");
    }

    [Fact]
    public void A_reserved_name_with_an_extension_is_still_reserved()
    {
        // "CON-report.html" still opens the console device.
        SafeFileName.For("CON.report").Should().StartWith("_");
    }

    [Fact]
    public void Trailing_dots_and_spaces_are_removed()
    {
        SafeFileName.For("Job 1 . . ").Should().Be("Job 1");
        SafeFileName.For("Job 1...").Should().Be("Job 1");
    }

    [Fact]
    public void Long_names_are_truncated_so_the_report_suffix_still_fits()
    {
        var name = new string('A', 400);

        SafeFileName.For(name).Length.Should().Be(80);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...")]
    [InlineData("///")]
    public void Empty_or_degenerate_names_fall_back_to_job(string name)
    {
        SafeFileName.For(name).Should().Be("job");
    }

    [Fact]
    public void Ordinary_names_are_left_alone()
    {
        SafeFileName.For("Sanjeevani Ladoo 1 (Marroon)").Should().Be("Sanjeevani Ladoo 1 (Marroon)");
    }
}
