using Inferno.Api.Services;
using Inferno.Common.Interfaces;
using Inferno.Common.Models;

namespace Inferno.Tests;

public class CookLoggerTests
{
    private class FakeSmoker : ISmoker
    {
        public SmokerMode Mode { get; set; } = SmokerMode.Ready;
        public int SetPoint { get; set; } = 225;
        public int PValue { get; set; } = 2;
        public Temps Temps { get; set; } = new Temps { GrillTemp = 70, ProbeTemp = 70 };
        public bool AugerOn { get; set; }
        public bool BlowerOn { get; set; }
        public bool IgniterOn { get; set; }
        public bool FireHealthy { get; set; } = true;
        public bool Preheated { get; set; }

        public SmokerStatus Status => new()
        {
            Mode = Mode.ToString(),
            SetPoint = SetPoint,
            PValue = PValue,
            Temps = Temps,
            AugerOn = AugerOn,
            BlowerOn = BlowerOn,
            IgniterOn = IgniterOn,
            FireHealthy = FireHealthy,
            Preheated = Preheated,
        };

        public bool SetMode(SmokerMode mode)
        {
            Mode = mode;
            return true;
        }

        public void SetGrill(double t) => Temps = new Temps { GrillTemp = t, ProbeTemp = Temps.ProbeTemp };
        public void SetProbe(double t) => Temps = new Temps { GrillTemp = Temps.GrillTemp, ProbeTemp = t };
    }

    static SqliteCookLogStore NewStore()
    {
        var store = new SqliteCookLogStore($"file:cooklogger-{Guid.NewGuid():N}?mode=memory&cache=shared");
        store.Initialize();
        return store;
    }

    static CookLogger NewLogger(ISmoker smoker, SqliteCookLogStore store, int flushThreshold = 6)
        => new(smoker, store, () => DateTime.UtcNow, autoStart: false, flushThreshold: flushThreshold);

    [Theory]
    [InlineData(SmokerMode.Smoke)]
    [InlineData(SmokerMode.Hold)]
    [InlineData(SmokerMode.Sear)]
    public void Tick_OpensSession_OnEntryToCookingMode(SmokerMode cookingMode)
    {
        var smoker = new FakeSmoker { Mode = SmokerMode.Ready };
        using var store = NewStore();
        var logger = NewLogger(smoker, store);

        Assert.Null(store.GetActiveSessionId());

        smoker.Mode = cookingMode;
        logger.Tick();

        Assert.NotNull(store.GetActiveSessionId());
        Assert.Single(store.ListSessions());
    }

    [Fact]
    public void Tick_DoesNotOpenSecondSession_OnFlipsBetweenCookingModes()
    {
        var smoker = new FakeSmoker { Mode = SmokerMode.Smoke };
        using var store = NewStore();
        var logger = NewLogger(smoker, store);

        logger.Tick();                       // opens
        smoker.Mode = SmokerMode.Hold;
        logger.Tick();
        smoker.Mode = SmokerMode.Sear;
        logger.Tick();

        Assert.Single(store.ListSessions());
        Assert.NotNull(store.GetActiveSessionId());
    }

    [Fact]
    public void Tick_ClosesSession_OnReturnToReady_WithSummary()
    {
        var smoker = new FakeSmoker { Mode = SmokerMode.Hold };
        using var store = NewStore();
        var logger = NewLogger(smoker, store, flushThreshold: 1);

        smoker.SetGrill(220);
        logger.Tick();                       // opens + 1 sample
        smoker.SetGrill(260);                // peak grill
        logger.Tick();

        smoker.Mode = SmokerMode.Shutdown;   // session stays open through cooldown
        logger.Tick();
        Assert.NotNull(store.GetActiveSessionId());

        smoker.Mode = SmokerMode.Ready;      // closes
        logger.Tick();

        Assert.Null(store.GetActiveSessionId());
        var session = store.ListSessions().Single();
        Assert.NotNull(session.EndTime);
        Assert.Equal(260, session.PeakGrillTemp);
        Assert.Equal(3, session.SampleCount);
    }

    [Theory]
    [InlineData(SmokerMode.Ready)]
    [InlineData(SmokerMode.Shutdown)]
    [InlineData(SmokerMode.Error)]
    public void Tick_DoesNotOpenSession_WhenNotCooking(SmokerMode idleMode)
    {
        var smoker = new FakeSmoker { Mode = idleMode };
        using var store = NewStore();
        var logger = NewLogger(smoker, store);

        logger.Tick();
        logger.Tick();

        Assert.Empty(store.ListSessions());
        Assert.Null(store.GetActiveSessionId());
    }

    [Fact]
    public void Startup_OpensSession_WhenConstructedMidCook()
    {
        var smoker = new FakeSmoker { Mode = SmokerMode.Hold };
        using var store = NewStore();
        var logger = NewLogger(smoker, store);

        // No active session existed in the store, so the baseline is Ready and the
        // first Tick detects the (Ready -> Hold) entry and opens a session.
        logger.Tick();

        Assert.NotNull(store.GetActiveSessionId());
        Assert.Single(store.ListSessions());
    }

    [Fact]
    public void Startup_ResumesExistingSession_WhenRestartedMidCook()
    {
        using var store = NewStore();
        long existing = store.OpenSession(DateTime.UtcNow, null);

        var smoker = new FakeSmoker { Mode = SmokerMode.Hold };
        var logger = NewLogger(smoker, store);

        logger.Tick();

        // Adopted the open session rather than opening a new one.
        Assert.Equal(existing, store.GetActiveSessionId());
        Assert.Single(store.ListSessions());
    }

    [Fact]
    public void Startup_ClosesOrphanSession_WhenRestartedIdle()
    {
        using var store = NewStore();
        long orphan = store.OpenSession(DateTime.UtcNow, null);

        var smoker = new FakeSmoker { Mode = SmokerMode.Ready };
        NewLogger(smoker, store);

        Assert.Null(store.GetActiveSessionId());
        Assert.NotNull(store.GetSession(orphan)!.EndTime);
    }

    [Fact]
    public void Tick_MapsStatusFields_IntoSample()
    {
        var smoker = new FakeSmoker
        {
            Mode = SmokerMode.Hold,
            SetPoint = 250,
            PValue = 4,
            AugerOn = true,
            BlowerOn = true,
            IgniterOn = false,
            FireHealthy = true,
            Preheated = true,
            Temps = new Temps { GrillTemp = 248, ProbeTemp = 165 },
        };
        using var store = NewStore();
        var logger = NewLogger(smoker, store, flushThreshold: 1);

        logger.Tick();

        var sample = store.GetSamples(store.GetActiveSessionId()!.Value, null, null).Single();
        Assert.Equal(248, sample.GrillTemp);
        Assert.Equal(165, sample.ProbeTemp);
        Assert.Equal("Hold", sample.Mode);
        Assert.Equal(250, sample.SetPoint);
        Assert.Equal(4, sample.PValue);
        Assert.True(sample.AugerOn);
        Assert.True(sample.BlowerOn);
        Assert.False(sample.IgniterOn);
        Assert.True(sample.FireHealthy);
        Assert.True(sample.Preheated);
    }

    [Fact]
    public void Buffer_FlushesOnlyAfterThreshold()
    {
        var smoker = new FakeSmoker { Mode = SmokerMode.Hold };
        using var store = NewStore();
        var logger = NewLogger(smoker, store, flushThreshold: 3);

        logger.Tick();
        logger.Tick();
        long sessionId = store.GetActiveSessionId()!.Value;
        // Two samples buffered, below the threshold of 3 -> nothing persisted yet.
        Assert.Empty(store.GetSamples(sessionId, null, null));

        logger.Tick();
        // Third tick hits the threshold and flushes the batch.
        Assert.Equal(3, store.GetSamples(sessionId, null, null).Count);
    }

    [Fact]
    public void Dispose_FlushesBuffer_AndForceClosesOpenSession()
    {
        var smoker = new FakeSmoker { Mode = SmokerMode.Hold };
        using var store = NewStore();
        var logger = NewLogger(smoker, store, flushThreshold: 100);

        logger.Tick();
        logger.Tick();
        long sessionId = store.GetActiveSessionId()!.Value;
        Assert.Empty(store.GetSamples(sessionId, null, null));   // still buffered

        logger.Dispose();

        Assert.Null(store.GetActiveSessionId());                 // session closed
        Assert.NotNull(store.GetSession(sessionId)!.EndTime);
        Assert.Equal(2, store.GetSamples(sessionId, null, null).Count);  // buffer flushed
    }

    [Fact]
    public void Tick_TracksPeakTemps_AcrossSamples()
    {
        var smoker = new FakeSmoker { Mode = SmokerMode.Hold };
        using var store = NewStore();
        var logger = NewLogger(smoker, store, flushThreshold: 1);

        smoker.Temps = new Temps { GrillTemp = 200, ProbeTemp = 100 };
        logger.Tick();
        smoker.Temps = new Temps { GrillTemp = 275, ProbeTemp = 150 };  // peaks
        logger.Tick();
        smoker.Temps = new Temps { GrillTemp = 240, ProbeTemp = 120 };
        logger.Tick();

        smoker.Mode = SmokerMode.Ready;
        logger.Tick();   // closes, writing the summary

        var session = store.ListSessions().Single();
        Assert.Equal(275, session.PeakGrillTemp);
        Assert.Equal(150, session.PeakProbeTemp);
    }
}
