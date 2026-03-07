using Inferno.Api.Interfaces;
using Inferno.Api.Services;
using Inferno.Common.Interfaces;
using Inferno.Common.Models;

namespace Inferno.Tests;

public class FireMinderTests
{
    private class FakeRelay : IRelayDevice
    {
        public bool IsOn { get; private set; }
        public void On() => IsOn = true;
        public void Off() => IsOn = false;
    }

    private class FakeSmoker : ISmoker
    {
        public SmokerMode Mode { get; set; } = SmokerMode.Ready;
        public int SetPoint { get; set; } = 225;
        public int PValue { get; set; } = 2;
        public Temps Temps { get; set; } = new Temps { GrillTemp = 70, ProbeTemp = 70 };
        public SmokerStatus Status => new SmokerStatus
        {
            Mode = Mode.ToString(),
            SetPoint = SetPoint,
            Temps = Temps
        };

        public bool SetMode(SmokerMode mode)
        {
            Mode = mode;
            return true;
        }
    }

    [Fact]
    public void GetFireCheckTemp_SmokeMode_Returns140()
    {
        var smoker = new FakeSmoker { Mode = SmokerMode.Smoke };
        var igniter = new FakeRelay();
        var fm = new FireMinder(smoker, igniter);

        Assert.Equal(140, fm.GetFireCheckTemp());
    }

    [Theory]
    [InlineData(225)]
    [InlineData(300)]
    [InlineData(400)]
    public void GetFireCheckTemp_HoldMode_ReturnsExpected(int setPoint)
    {
        var smoker = new FakeSmoker { Mode = SmokerMode.Hold, SetPoint = setPoint };
        var igniter = new FakeRelay();
        var fm = new FireMinder(smoker, igniter);

        int expected = setPoint - (setPoint / 180 * 30);
        Assert.Equal(expected, fm.GetFireCheckTemp());
    }

    [Fact]
    public void InitialState_FireNotStarted()
    {
        var smoker = new FakeSmoker();
        var igniter = new FakeRelay();
        var fm = new FireMinder(smoker, igniter);

        Assert.False(fm.IsFireStarted);
        Assert.True(fm.IsFireHealthy);
        Assert.False(fm.IsReigniting);
    }

    [Fact]
    public void ResetFireStatus_ClearsState()
    {
        var smoker = new FakeSmoker();
        var igniter = new FakeRelay();
        var fm = new FireMinder(smoker, igniter);

        fm.ResetFireStatus();

        Assert.False(fm.IsFireStarted);
        Assert.True(fm.IsFireHealthy);
    }
}
