using Inferno.Api.Pid;

namespace Inferno.Tests;

public class SmokerPidTests
{
    [Fact]
    public void GetControlVariable_BelowSetPoint_ReturnsPositive()
    {
        var pid = new SmokerPid(60.0, 180.0, 45.0);
        pid.SetPoint = 225;
        // Prime with setpoint temp to initialize _lastUpdate/_lastTemp
        pid.GetControlVariable(225);
        Thread.Sleep(1100); // dT.Seconds must be >= 1 to avoid division by zero

        double u = pid.GetControlVariable(200);
        Assert.True(u > 0, $"Expected positive control variable when below setpoint, got {u}");
    }

    [Fact]
    public void GetControlVariable_AboveSetPoint_ReturnsNegative()
    {
        var pid = new SmokerPid(60.0, 180.0, 45.0);
        pid.SetPoint = 225;
        pid.GetControlVariable(225);
        Thread.Sleep(1100);

        double u = pid.GetControlVariable(250);
        Assert.True(u < 0, $"Expected negative control variable when above setpoint, got {u}");
    }

    [Fact]
    public void GetControlVariable_NaN_ReturnsZero()
    {
        var pid = new SmokerPid(60.0, 180.0, 45.0);
        pid.SetPoint = 225;
        double u = pid.GetControlVariable(double.NaN);
        Assert.Equal(0, u);
    }

    [Fact]
    public void SetPoint_CanBeUpdated()
    {
        var pid = new SmokerPid(60.0, 180.0, 45.0);
        pid.SetPoint = 300;
        Assert.Equal(300, pid.SetPoint);
    }
}
