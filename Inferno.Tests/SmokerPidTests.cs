using Inferno.Api.Pid;

namespace Inferno.Tests;

public class SmokerPidTests
{
    [Fact]
    public void GetControlVariable_BelowSetPoint_ReturnsPositive()
    {
        var pid = new SmokerPid(60.0, 180.0, 45.0);
        pid.SetPoint = 225;
        pid.GetControlVariable(225);
        Thread.Sleep(100);

        double u = pid.GetControlVariable(200);
        Assert.True(u > 0, $"Expected positive control variable when below setpoint, got {u}");
    }

    [Fact]
    public void GetControlVariable_AboveSetPoint_ReturnsNegative()
    {
        var pid = new SmokerPid(60.0, 180.0, 45.0);
        pid.SetPoint = 225;
        pid.GetControlVariable(225);
        Thread.Sleep(100);

        double u = pid.GetControlVariable(250);
        Assert.True(u < 0, $"Expected negative control variable when above setpoint, got {u}");
    }

    [Fact]
    public void GetControlVariable_AtSetPoint_ReturnsNearZero()
    {
        var pid = new SmokerPid(60.0, 180.0, 45.0);
        pid.SetPoint = 225;
        pid.GetControlVariable(225);
        Thread.Sleep(100);

        double u = pid.GetControlVariable(225);
        Assert.InRange(u, -0.1, 0.1);
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
    public void GetControlVariable_NaN_DoesNotCorruptState()
    {
        var pid = new SmokerPid(60.0, 180.0, 45.0);
        pid.SetPoint = 225;
        pid.GetControlVariable(225);
        Thread.Sleep(100);

        // Inject NaN — should not corrupt internal state
        pid.GetControlVariable(double.NaN);
        Thread.Sleep(100);

        // Next valid call should still behave reasonably
        double u = pid.GetControlVariable(200);
        Assert.True(u > 0, $"Expected positive control variable after NaN recovery, got {u}");
        Assert.False(double.IsNaN(u), "Control variable should not be NaN after NaN recovery");
        Assert.False(double.IsInfinity(u), "Control variable should not be Infinity after NaN recovery");
    }

    [Fact]
    public void GetControlVariable_TwoCallsSameInstant_ReturnsFinite()
    {
        var pid = new SmokerPid(60.0, 180.0, 45.0);
        pid.SetPoint = 225;

        // Back-to-back calls with effectively zero elapsed time must not divide by
        // zero in the derivative term and produce NaN/Infinity.
        pid.GetControlVariable(200); // seed
        double u = pid.GetControlVariable(200);

        Assert.False(double.IsNaN(u), $"Control variable should not be NaN, got {u}");
        Assert.False(double.IsInfinity(u), $"Control variable should not be Infinity, got {u}");
    }

    [Fact]
    public void SetPoint_CanBeUpdated()
    {
        var pid = new SmokerPid(60.0, 180.0, 45.0);
        pid.SetPoint = 300;
        Assert.Equal(300, pid.SetPoint);
    }
}
