using Inferno.Api.Devices;

namespace Inferno.Tests;

public class RtdArrayTests
{
    [Theory]
    [InlineData(512, 998.0)]     // Mid-range ADC → ~998Ω
    [InlineData(1, 1022000.0)]   // Near-zero ADC → very high resistance
    public void CalculateResistanceFromAdc_ReturnsExpectedResistance(int adcValue, double expectedApprox)
    {
        double resistance = RtdArray.CalculateResistanceFromAdc(adcValue);

        Assert.True(resistance > 0, $"Resistance should be positive, got {resistance}");
        Assert.Equal(expectedApprox, resistance, precision: 0);
    }

    [Fact]
    public void CalculateResistanceFromAdc_Zero_ReturnsInfinity()
    {
        double resistance = RtdArray.CalculateResistanceFromAdc(0);
        Assert.True(Double.IsInfinity(resistance));
    }

    [Fact]
    public void RtdTempFahrenheit_KnownResistance_ReturnsExpectedTemp()
    {
        // PT100 with 1000Ω reference: 1000Ω at 0°C = 32°F
        double tempF = RtdArray.RtdTempFahrenheitFromResistance(1000);
        Assert.Equal(32, tempF, precision: 0);
    }

    [Fact]
    public void RtdTempFahrenheit_HigherResistance_ReturnsHigherTemp()
    {
        double tempLow = RtdArray.RtdTempFahrenheitFromResistance(1000);
        double tempHigh = RtdArray.RtdTempFahrenheitFromResistance(1100);
        Assert.True(tempHigh > tempLow);
    }

    [Fact]
    public void RtdTempFahrenheit_TypicalGrillTemp()
    {
        // ~1385Ω should be in the 200-250°F range for this RTD configuration
        double tempF = RtdArray.RtdTempFahrenheitFromResistance(1385);
        Assert.InRange(tempF, 200, 250);
    }

    [Theory]
    [InlineData(0)]    // Division by zero → Infinity resistance
    [InlineData(1023)] // Max ADC → near-zero resistance
    public void AdcToTemp_BoundaryValues_DoNotCrash(int adcValue)
    {
        double resistance = RtdArray.CalculateResistanceFromAdc(adcValue);
        double tempF = RtdArray.RtdTempFahrenheitFromResistance(resistance);
        // Should not throw — result may be NaN/Infinity, handled by validation layer
    }

    [Fact]
    public void EnqueueIfValid_ValidReading_EnqueuesAndResetsCounter()
    {
        var queue = new System.Collections.Concurrent.ConcurrentQueue<double>();
        int invalid = 5;

        RtdArray.EnqueueIfValid(queue, 512, "Test", ref invalid); // ~998Ω, valid

        Assert.Single(queue);
        Assert.Equal(0, invalid);
    }

    [Fact]
    public void EnqueueIfValid_BriefInvalidBurst_KeepsStaleData()
    {
        var queue = new System.Collections.Concurrent.ConcurrentQueue<double>();
        int invalid = 0;

        // A good reading, then a short burst of bad ones (noise) — the good sample
        // is retained so the average rides through the spike.
        RtdArray.EnqueueIfValid(queue, 512, "Test", ref invalid);
        for (int i = 0; i < 10; i++)
            RtdArray.EnqueueIfValid(queue, 0, "Test", ref invalid); // open circuit

        Assert.False(queue.IsEmpty);
    }

    [Fact]
    public void EnqueueIfValid_SustainedInvalid_DrainsForUnpluggedDetection()
    {
        var queue = new System.Collections.Concurrent.ConcurrentQueue<double>();
        int invalid = 0;

        RtdArray.EnqueueIfValid(queue, 512, "Test", ref invalid); // seed a good sample

        // A sustained run of open-circuit readings = sensor unplugged → drain so the
        // temperature reads NaN (surfaced downstream as "Unplugged").
        for (int i = 0; i < RtdArray.MaxConsecutiveInvalidReadings; i++)
            RtdArray.EnqueueIfValid(queue, 0, "Test", ref invalid);

        Assert.True(queue.IsEmpty);
    }
}
