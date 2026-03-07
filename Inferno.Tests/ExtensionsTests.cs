using Inferno.Common.Extensions;
using Inferno.Common.Models;

namespace Inferno.Tests;

public class ExtensionsTests
{
    [Theory]
    [InlineData(5.0, 0.0, 10.0, 5.0)]   // Within range
    [InlineData(-1.0, 0.0, 10.0, 0.0)]   // Below min
    [InlineData(15.0, 0.0, 10.0, 10.0)]  // Above max
    [InlineData(0.0, 0.0, 10.0, 0.0)]    // At min
    [InlineData(10.0, 0.0, 10.0, 10.0)]  // At max
    public void Clamp_Double_ClampsCorrectly(double value, double min, double max, double expected)
    {
        Assert.Equal(expected, value.Clamp(min, max));
    }

    [Theory]
    [InlineData(5, 0, 10, 5)]
    [InlineData(-1, 0, 10, 0)]
    [InlineData(15, 0, 10, 10)]
    public void Clamp_Int_ClampsCorrectly(int value, int min, int max, int expected)
    {
        Assert.Equal(expected, value.Clamp(min, max));
    }

    [Theory]
    [InlineData(SmokerMode.Smoke, true)]
    [InlineData(SmokerMode.Hold, true)]
    [InlineData(SmokerMode.Sear, true)]
    [InlineData(SmokerMode.Ready, false)]
    [InlineData(SmokerMode.Shutdown, false)]
    [InlineData(SmokerMode.Error, false)]
    public void IsCookingMode_ReturnsExpected(SmokerMode mode, bool expected)
    {
        Assert.Equal(expected, mode.IsCookingMode());
    }
}
