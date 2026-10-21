using DevDbStudio.Services;

namespace DevDbStudio.Tests;

public class DistroParsingTests
{
    [Fact]
    public void ParseDistros_KeepsOrder_AndSkipsDockerDesktopHelpers()
    {
        var output = "Ubuntu\r\ndocker-desktop\r\npodman-machine-default\r\ndocker-desktop-data\r\n";

        Assert.Equal(["Ubuntu", "podman-machine-default"], ContainerEngines.ParseDistros(output));
    }

    [Fact]
    public void ParseDistros_HandlesBlankAndDuplicateLines()
    {
        Assert.Equal(["Debian"], ContainerEngines.ParseDistros("\n Debian \n\ndebian\n"));
        Assert.Empty(ContainerEngines.ParseDistros(""));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("auto", true)]
    [InlineData("Auto", true)]
    [InlineData("Ubuntu", false)]
    public void IsAuto_RecognisesAutoDetection(string? value, bool expected) =>
        Assert.Equal(expected, ContainerEngines.IsAuto(value));
}
