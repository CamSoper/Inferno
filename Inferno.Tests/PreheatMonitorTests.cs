using Inferno.Api.Services;

namespace Inferno.Tests;

public class PreheatMonitorTests
{
    [Fact]
    public void Update_And_Reset_Concurrently_DoNotThrow()
    {
        // Update() runs on the preheat loop while Reset() fires from API threads on a
        // mode change. The internal queue isn't concurrent, so hammer both from many
        // threads and assert the lock keeps it from throwing/corrupting.
        var monitor = new PreheatMonitor();
        var stop = DateTime.UtcNow + TimeSpan.FromMilliseconds(500);

        var updaters = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            var rng = new Random();
            while (DateTime.UtcNow < stop)
                monitor.Update(rng.Next(150, 230), 225, isCookingMode: true, isFireHealthy: true);
        }));

        var resetters = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            while (DateTime.UtcNow < stop)
                monitor.Reset();
        }));

        // Should complete without InvalidOperationException ("collection modified").
        Task.WaitAll(updaters.Concat(resetters).ToArray());
    }

    private static void FeedStableTemps(PreheatMonitor monitor, double temp, int setPoint, int count)
    {
        for (int i = 0; i < count; i++)
        {
            monitor.Update(temp, setPoint, isCookingMode: true, isFireHealthy: true);
        }
    }

    [Fact]
    public void Update_NotCookingMode_NotPreheated()
    {
        var monitor = new PreheatMonitor();

        for (int i = 0; i < PreheatMonitor.WindowSize; i++)
        {
            monitor.Update(225, 225, isCookingMode: false, isFireHealthy: true);
        }

        Assert.False(monitor.IsPreheated);
    }

    [Fact]
    public void Update_FireUnhealthy_NotPreheated()
    {
        var monitor = new PreheatMonitor();

        for (int i = 0; i < PreheatMonitor.WindowSize; i++)
        {
            monitor.Update(225, 225, isCookingMode: true, isFireHealthy: false);
        }

        Assert.False(monitor.IsPreheated);
    }

    [Fact]
    public void Update_InvalidTemp_NotPreheated()
    {
        var monitor = new PreheatMonitor();

        for (int i = 0; i < PreheatMonitor.WindowSize; i++)
        {
            monitor.Update(double.NaN, 225, isCookingMode: true, isFireHealthy: true);
        }

        Assert.False(monitor.IsPreheated);

        for (int i = 0; i < PreheatMonitor.WindowSize; i++)
        {
            monitor.Update(-1, 225, isCookingMode: true, isFireHealthy: true);
        }

        Assert.False(monitor.IsPreheated);
    }

    [Fact]
    public void Update_WindowNotFull_NotPreheated()
    {
        var monitor = new PreheatMonitor();
        FeedStableTemps(monitor, 225, 225, PreheatMonitor.WindowSize - 1);

        Assert.False(monitor.IsPreheated);
    }

    [Fact]
    public void Update_TempRangeTooWide_NotPreheated()
    {
        var monitor = new PreheatMonitor();

        // Simulate climbing temps with a wide range
        for (int i = 0; i < PreheatMonitor.WindowSize; i++)
        {
            double temp = 200 + (i * 0.5); // 200 to 229.5 = 29.5°F range
            monitor.Update(temp, 225, isCookingMode: true, isFireHealthy: true);
        }

        Assert.False(monitor.IsPreheated);
    }

    [Fact]
    public void Update_AvgBelowProximity_NotPreheated()
    {
        var monitor = new PreheatMonitor();

        // Stable at 200°F but setpoint is 400 → 200 < 360 (90% of 400)
        FeedStableTemps(monitor, 200, 400, PreheatMonitor.WindowSize);

        Assert.False(monitor.IsPreheated);
    }

    [Fact]
    public void Update_StableAndClose_BecomesPreheated()
    {
        var monitor = new PreheatMonitor();
        FeedStableTemps(monitor, 225, 225, PreheatMonitor.WindowSize);

        Assert.True(monitor.IsPreheated);
    }

    [Fact]
    public void Update_StableBelow_BecomesPreheated()
    {
        var monitor = new PreheatMonitor();

        // Stable at 390 with setpoint 400 → avg 390 >= 360 (90% of 400), range = 0
        FeedStableTemps(monitor, 390, 400, PreheatMonitor.WindowSize);

        Assert.True(monitor.IsPreheated);
    }

    [Fact]
    public void IsPreheated_Latches()
    {
        var monitor = new PreheatMonitor();
        FeedStableTemps(monitor, 225, 225, PreheatMonitor.WindowSize);
        Assert.True(monitor.IsPreheated);

        // Feed bad conditions — should stay latched
        monitor.Update(100, 225, isCookingMode: true, isFireHealthy: true);
        Assert.True(monitor.IsPreheated);

        monitor.Update(225, 225, isCookingMode: false, isFireHealthy: true);
        Assert.True(monitor.IsPreheated);

        monitor.Update(225, 225, isCookingMode: true, isFireHealthy: false);
        Assert.True(monitor.IsPreheated);
    }

    [Fact]
    public void Reset_ClearsLatch()
    {
        var monitor = new PreheatMonitor();
        FeedStableTemps(monitor, 225, 225, PreheatMonitor.WindowSize);
        Assert.True(monitor.IsPreheated);

        monitor.Reset();
        Assert.False(monitor.IsPreheated);

        // Should require a full window again
        FeedStableTemps(monitor, 225, 225, PreheatMonitor.WindowSize - 1);
        Assert.False(monitor.IsPreheated);

        monitor.Update(225, 225, isCookingMode: true, isFireHealthy: true);
        Assert.True(monitor.IsPreheated);
    }
}
