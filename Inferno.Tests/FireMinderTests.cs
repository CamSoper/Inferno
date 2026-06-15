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

    /// <summary>Mutable clock so tests can advance time between Tick() calls.</summary>
    private sealed class TestClock
    {
        public DateTime Now = new DateTime(2026, 1, 1, 12, 0, 0);
        public void Advance(TimeSpan t) => Now += t;
    }

    private static void SetGrill(FakeSmoker smoker, double temp) =>
        smoker.Temps = new Temps { GrillTemp = temp, ProbeTemp = temp };

    /// <summary>
    /// Builds a FireMinder that does NOT auto-run its loop, drives it to an
    /// "established healthy fire" state (Hold @ 225, check temp = 195), and returns
    /// the pieces so the test can manipulate temp/clock and call Tick() directly.
    /// </summary>
    private static (FireMinder fm, FakeSmoker smoker, FakeRelay igniter, TestClock clock) EstablishedFire()
    {
        var smoker = new FakeSmoker { Mode = SmokerMode.Hold, SetPoint = 225 };
        var igniter = new FakeRelay();
        var clock = new TestClock();
        var fm = new FireMinder(smoker, igniter, () => clock.Now, autoStart: false);
        fm.ResetFireStatus();

        // Two ticks above the check temp (195) proves the fire: first sets
        // _fireStarted, second clears _initialIgnition.
        SetGrill(smoker, 200);
        fm.Tick();
        fm.Tick();

        Assert.True(fm.IsFireStarted);
        Assert.True(fm.IsFireHealthy);
        Assert.False(igniter.IsOn);
        return (fm, smoker, igniter, clock);
    }

    [Fact]
    public void GetFireCheckTemp_SmokeMode_Returns140()
    {
        var smoker = new FakeSmoker { Mode = SmokerMode.Smoke };
        var igniter = new FakeRelay();
        var fm = new FireMinder(smoker, igniter, autoStart: false);

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
        var fm = new FireMinder(smoker, igniter, autoStart: false);

        int expected = setPoint - (setPoint / 180 * 30);
        Assert.Equal(expected, fm.GetFireCheckTemp());
    }

    [Fact]
    public void InitialState_FireNotStarted()
    {
        var smoker = new FakeSmoker();
        var igniter = new FakeRelay();
        var fm = new FireMinder(smoker, igniter, autoStart: false);

        Assert.False(fm.IsFireStarted);
        Assert.True(fm.IsFireHealthy);
        Assert.False(fm.IsReigniting);
        Assert.False(fm.IsLidOpen);
    }

    [Fact]
    public void ResetFireStatus_ClearsState()
    {
        var smoker = new FakeSmoker();
        var igniter = new FakeRelay();
        var fm = new FireMinder(smoker, igniter, autoStart: false);

        fm.ResetFireStatus();

        Assert.False(fm.IsFireStarted);
        Assert.True(fm.IsFireHealthy);
        Assert.False(fm.IsLidOpen);
    }

    [Fact]
    public void BriefDip_RecoversBeforeDebounce_DoesNotTrip()
    {
        var (fm, smoker, igniter, clock) = EstablishedFire();

        // Dip below check temp (195), but recover before the 45s debounce elapses.
        SetGrill(smoker, 180);
        fm.Tick();
        Assert.True(fm.IsFireHealthy);   // not declared unhealthy yet
        Assert.False(igniter.IsOn);

        clock.Advance(TimeSpan.FromSeconds(20));
        SetGrill(smoker, 200);           // back above check temp
        fm.Tick();

        Assert.True(fm.IsFireHealthy);
        Assert.False(igniter.IsOn);
        Assert.False(fm.IsReigniting);
    }

    [Fact]
    public void SustainedDecline_TripsAfterDebounce_AndLightsIgniterImmediately()
    {
        var (fm, smoker, igniter, clock) = EstablishedFire();

        SetGrill(smoker, 180);           // below check temp (195)
        fm.Tick();
        Assert.True(fm.IsFireHealthy);   // debounce not yet satisfied
        Assert.False(igniter.IsOn);

        clock.Advance(TimeSpan.FromSeconds(46)); // past the 45s debounce
        fm.Tick();

        Assert.False(fm.IsFireHealthy);  // declared unhealthy
        Assert.True(igniter.IsOn);       // igniter lit immediately, no reignite wait
        Assert.True(fm.IsReigniting);
    }

    [Fact]
    public void Recovery_TempBackAboveCheck_TurnsIgniterOffAndHealthy()
    {
        var (fm, smoker, igniter, clock) = EstablishedFire();

        SetGrill(smoker, 180);
        fm.Tick();
        clock.Advance(TimeSpan.FromSeconds(46));
        fm.Tick();
        Assert.True(fm.IsReigniting);

        // Fire recovers above the check temp.
        SetGrill(smoker, 200);
        fm.Tick();

        Assert.True(fm.IsFireHealthy);
        Assert.False(igniter.IsOn);
        Assert.False(fm.IsReigniting);
    }

    [Fact]
    public void SustainedDecline_PastFireTimeout_SetsError()
    {
        var (fm, smoker, igniter, clock) = EstablishedFire();

        SetGrill(smoker, 180);
        fm.Tick();
        clock.Advance(TimeSpan.FromSeconds(46));
        fm.Tick();
        Assert.True(fm.IsReigniting);

        // Never recovers — past the 10 minute fire timeout.
        clock.Advance(TimeSpan.FromMinutes(11));
        fm.Tick();

        Assert.Equal(SmokerMode.Error, smoker.Mode);
    }

    [Fact]
    public void SlowlyClimbingFire_DoesNotError_ThenRecovers()
    {
        var (fm, smoker, igniter, clock) = EstablishedFire();

        // Drop well below check temp (195) and trip recovery.
        SetGrill(smoker, 160);
        fm.Tick();
        clock.Advance(TimeSpan.FromSeconds(46));
        fm.Tick();
        Assert.True(fm.IsReigniting);

        // Climb slowly: +6F every 3.5 min, staying below the check temp. Total elapsed
        // (~17 min) far exceeds the 10 min fire/igniter timeouts, but each step is
        // upward progress so the give-up clocks keep resetting.
        foreach (var temp in new double[] { 166, 172, 178, 184, 190 })
        {
            clock.Advance(TimeSpan.FromSeconds(210));
            SetGrill(smoker, temp);
            fm.Tick();

            Assert.NotEqual(SmokerMode.Error, smoker.Mode);
            Assert.True(fm.IsReigniting);
        }

        // Finally climbs back above the check temp — fully recovered.
        SetGrill(smoker, 196);
        fm.Tick();

        Assert.True(fm.IsFireHealthy);
        Assert.False(igniter.IsOn);
        Assert.NotEqual(SmokerMode.Error, smoker.Mode);
    }

    [Fact]
    public void StalledFire_NoProgress_StillErrors()
    {
        var (fm, smoker, igniter, clock) = EstablishedFire();

        SetGrill(smoker, 160);
        fm.Tick();
        clock.Advance(TimeSpan.FromSeconds(46));
        fm.Tick();
        Assert.True(fm.IsReigniting);

        // Flat (no upward progress) for the whole fire timeout → give up.
        clock.Advance(TimeSpan.FromMinutes(11));
        fm.Tick();

        Assert.Equal(SmokerMode.Error, smoker.Mode);
    }

    [Fact]
    public void SharpDropDuringRecovery_StaysOnRecovery_NotReportedAsLid()
    {
        var (fm, smoker, igniter, clock) = EstablishedFire();

        // Trip into recovery.
        SetGrill(smoker, 180);
        fm.Tick();
        clock.Advance(TimeSpan.FromSeconds(46));
        fm.Tick();
        Assert.True(fm.IsReigniting);

        // Fill the lid window at the recovery temp, then a sharp further drop that
        // would latch the LidMonitor internally.
        for (int i = 0; i < LidMonitor.WindowSize; i++)
        {
            SetGrill(smoker, 180);
            fm.Tick();
        }
        SetGrill(smoker, 145); // 35F cliff while already recovering
        fm.Tick();

        // Recovery dominates: lid is NOT reported, and we stay reigniting so the
        // Smoker keeps the aggressive recovery feed (not the maintenance floor).
        Assert.False(fm.IsLidOpen);
        Assert.True(fm.IsReigniting);
        Assert.True(igniter.IsOn);
    }

    [Fact]
    public void LidOpen_SuppressesFireHealth_NoTripNoIgniter()
    {
        var (fm, smoker, igniter, clock) = EstablishedFire();

        // Fill the lid window with a steady temp, then drop sharply (>=30F) to
        // simulate the lid opening.
        for (int i = 0; i < LidMonitor.WindowSize; i++)
        {
            SetGrill(smoker, 200);
            fm.Tick();
        }
        SetGrill(smoker, 165); // 35F cliff
        fm.Tick();
        Assert.True(fm.IsLidOpen);

        // Even though temp (165) is below the check temp (195), and time passes,
        // the fire is NOT declared unhealthy and the igniter stays off.
        clock.Advance(TimeSpan.FromSeconds(60));
        fm.Tick();

        Assert.True(fm.IsLidOpen);
        Assert.True(fm.IsFireHealthy);
        Assert.False(igniter.IsOn);
        Assert.False(fm.IsReigniting);
    }
}
