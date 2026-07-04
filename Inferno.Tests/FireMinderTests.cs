using Inferno.Api.Interfaces;
using Inferno.Api.Services;
using Inferno.Common.Interfaces;
using Inferno.Common.Models;
using Microsoft.Extensions.Time.Testing;

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

    private static void SetGrill(FakeSmoker smoker, double temp) =>
        smoker.Temps = new Temps { GrillTemp = temp, ProbeTemp = temp };

    /// <summary>
    /// Builds a FireMinder that does NOT auto-run its loop, drives it to an
    /// "established healthy fire" state (Hold @ 225, check temp = 187), and returns
    /// the pieces so the test can manipulate temp/clock and call Tick() directly.
    /// </summary>
    private static (FireMinder fm, FakeSmoker smoker, FakeRelay igniter, FakeTimeProvider clock) EstablishedFire()
    {
        var smoker = new FakeSmoker { Mode = SmokerMode.Hold, SetPoint = 225 };
        var igniter = new FakeRelay();
        var clock = new FakeTimeProvider();
        var fm = new FireMinder(smoker, igniter, clock, autoStart: false);
        fm.ResetFireStatus();

        // Two ticks above the check temp (187) proves the fire: first sets
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
    public void SlowColdStart_KeepsClimbing_DoesNotErrorBeforeIgnition()
    {
        // Cold start in Hold: igniter lights and the grill climbs slowly toward the
        // 150F ignition threshold, taking far longer than the 10 minute igniter
        // timeout. Each step is upward progress, so the give-up clock keeps resetting
        // instead of killing a fire that's plainly catching.
        var smoker = new FakeSmoker { Mode = SmokerMode.Hold, SetPoint = 250 };
        var igniter = new FakeRelay();
        var clock = new FakeTimeProvider();
        var fm = new FireMinder(smoker, igniter, clock, autoStart: false);
        fm.ResetFireStatus();

        SetGrill(smoker, 75);
        fm.Tick();
        Assert.True(igniter.IsOn);   // igniter lit for the cold start

        // Climb +6F every 3.5 min from 75 toward 150. Total elapsed (~16 min) exceeds
        // the 10 min igniter timeout, but steady progress keeps it alive.
        foreach (var temp in new double[] { 81, 87, 93, 99, 105, 111, 117, 123, 129, 135, 141, 147 })
        {
            clock.Advance(TimeSpan.FromSeconds(210));
            SetGrill(smoker, temp);
            fm.Tick();

            Assert.NotEqual(SmokerMode.Error, smoker.Mode);
            Assert.True(igniter.IsOn);
            Assert.False(fm.IsFireStarted);
        }

        // Crosses the ignition threshold — fire is established, igniter off.
        SetGrill(smoker, 152);
        fm.Tick();

        Assert.True(fm.IsFireStarted);
        Assert.False(igniter.IsOn);
        Assert.NotEqual(SmokerMode.Error, smoker.Mode);
    }

    [Fact]
    public void ColdStart_NoProgress_StillTimesOutToError()
    {
        // A genuinely dead light: igniter on, grill never rises. The progress reset
        // never triggers, so the fixed igniter timeout still gives up.
        var smoker = new FakeSmoker { Mode = SmokerMode.Hold, SetPoint = 250 };
        var igniter = new FakeRelay();
        var clock = new FakeTimeProvider();
        var fm = new FireMinder(smoker, igniter, clock, autoStart: false);
        fm.ResetFireStatus();

        SetGrill(smoker, 75);
        fm.Tick();
        Assert.True(igniter.IsOn);

        // Flat for the whole igniter timeout → give up.
        clock.Advance(TimeSpan.FromMinutes(11));
        fm.Tick();

        Assert.Equal(SmokerMode.Error, smoker.Mode);
        Assert.False(igniter.IsOn);
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
    [InlineData(359)]
    [InlineData(360)]
    [InlineData(400)]
    public void GetFireCheckTemp_HoldMode_ReturnsExpected(int setPoint)
    {
        var smoker = new FakeSmoker { Mode = SmokerMode.Hold, SetPoint = setPoint };
        var igniter = new FakeRelay();
        var fm = new FireMinder(smoker, igniter, autoStart: false);

        // 5/6 of the setpoint (a 30F margin at the 180F floor), as a smooth curve.
        int expected = (int)(setPoint * (150.0 / 180.0));
        Assert.Equal(expected, fm.GetFireCheckTemp());
    }

    [Fact]
    public void GetFireCheckTemp_HoldMode_HasNoCliffAtSetpoint360()
    {
        // Regression guard: the old integer `SetPoint / 180` made this a step function
        // with a ~30F jump between setpoint 359 and 360. The proportional formula must
        // stay continuous there.
        var igniter = new FakeRelay();
        var below = new FireMinder(new FakeSmoker { Mode = SmokerMode.Hold, SetPoint = 359 }, igniter, autoStart: false);
        var at = new FireMinder(new FakeSmoker { Mode = SmokerMode.Hold, SetPoint = 360 }, igniter, autoStart: false);

        Assert.True(Math.Abs(at.GetFireCheckTemp() - below.GetFireCheckTemp()) <= 2,
            $"Expected a smooth threshold across setpoint 360, got {below.GetFireCheckTemp()} → {at.GetFireCheckTemp()}");
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
    public void InitialIgnitionTemp_DefaultsToFloor_BeforeFireStarts()
    {
        var smoker = new FakeSmoker { Mode = SmokerMode.Sear, SetPoint = 400 };
        var igniter = new FakeRelay();
        var fm = new FireMinder(smoker, igniter, autoStart: false);
        fm.ResetFireStatus();

        // Before the fire catches, the anchor sits at the 150F floor so Sear's
        // relative establish gate stays conservative.
        Assert.False(fm.IsFireStarted);
        Assert.Equal(150, fm.InitialIgnitionTemp);
    }

    [Fact]
    public void ColdStart_CapturesInitialIgnitionTempAtCatch()
    {
        var smoker = new FakeSmoker { Mode = SmokerMode.Sear, SetPoint = 400 };
        var igniter = new FakeRelay();
        var clock = new FakeTimeProvider();
        var fm = new FireMinder(smoker, igniter, clock, autoStart: false);
        fm.ResetFireStatus();

        SetGrill(smoker, 75);
        fm.Tick();                       // igniter lights; ignition temp floors at 150
        Assert.True(igniter.IsOn);
        Assert.False(fm.IsFireStarted);

        SetGrill(smoker, 152);
        fm.Tick();                       // crosses 150 → fire started

        Assert.True(fm.IsFireStarted);
        Assert.Equal(150, fm.InitialIgnitionTemp);
    }

    [Fact]
    public void InitialIgnitionTemp_UnchangedByRecoveryRelight()
    {
        // EstablishedFire catches cold (grill jumps to 200 over the 150 floor).
        var (fm, smoker, igniter, clock) = EstablishedFire();
        Assert.Equal(150, fm.InitialIgnitionTemp);

        // Trip recovery: the relight raises the internal ignition threshold to the
        // fire-check temp (187), but the initial-catch anchor must stay put so Sear's
        // establish gate doesn't drift upward after a recovery.
        SetGrill(smoker, 180);
        fm.Tick();
        clock.Advance(TimeSpan.FromSeconds(46));
        fm.Tick();
        Assert.True(fm.IsReigniting);

        Assert.Equal(150, fm.InitialIgnitionTemp);
    }

    [Fact]
    public void BriefDip_RecoversBeforeDebounce_DoesNotTrip()
    {
        var (fm, smoker, igniter, clock) = EstablishedFire();

        // Dip below check temp (187), but recover before the 45s debounce elapses.
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

        SetGrill(smoker, 180);           // below check temp (187)
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

        // Drop well below check temp (187) and trip recovery.
        SetGrill(smoker, 160);
        fm.Tick();
        clock.Advance(TimeSpan.FromSeconds(46));
        fm.Tick();
        Assert.True(fm.IsReigniting);

        // Climb slowly: +6F every 3.5 min, staying below the check temp. Total elapsed
        // (~14 min) far exceeds the 10 min fire/igniter timeouts, but each step is
        // upward progress so the give-up clocks keep resetting.
        foreach (var temp in new double[] { 166, 172, 178, 184 })
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

        // Even though temp (165) is below the check temp (187), and time passes,
        // the fire is NOT declared unhealthy and the igniter stays off.
        clock.Advance(TimeSpan.FromSeconds(60));
        fm.Tick();

        Assert.True(fm.IsLidOpen);
        Assert.True(fm.IsFireHealthy);
        Assert.False(igniter.IsOn);
        Assert.False(fm.IsReigniting);
    }

    [Fact]
    public void SensorFault_SustainedInvalidGrill_FailsSafeToError()
    {
        var (fm, smoker, igniter, clock) = EstablishedFire();

        // The grill sensor drops out — the Smoker surfaces -1. A sustained fault must
        // fail safe to Error rather than driving the aggressive recovery feed off a
        // garbage temperature (the old behavior: -1 read as a dying fire → reignite).
        for (int i = 0; i < 5; i++) // SensorFaultTicks
        {
            Assert.NotEqual(SmokerMode.Error, smoker.Mode);
            SetGrill(smoker, -1);
            fm.Tick();
        }

        Assert.Equal(SmokerMode.Error, smoker.Mode);
        Assert.False(igniter.IsOn);
        Assert.False(fm.IsReigniting);
    }

    [Fact]
    public void SensorFault_BriefInvalidBurst_DoesNotTrip()
    {
        var (fm, smoker, igniter, clock) = EstablishedFire();

        // A short burst of invalid readings (below the fault threshold) followed by a
        // good one: the counter resets and the cook continues untouched.
        SetGrill(smoker, -1);
        fm.Tick();
        SetGrill(smoker, -1);
        fm.Tick();
        SetGrill(smoker, 200);
        fm.Tick();

        Assert.NotEqual(SmokerMode.Error, smoker.Mode);
        Assert.True(fm.IsFireHealthy);
        Assert.False(igniter.IsOn);
    }
}
