using Inferno.Api.Services;

namespace Inferno.Tests;

public class LidMonitorTests
{
    private static void FeedStable(LidMonitor monitor, double temp, int count)
    {
        for (int i = 0; i < count; i++)
            monitor.Update(temp);
    }

    [Fact]
    public void InitialState_NotOpen()
    {
        var monitor = new LidMonitor();
        Assert.False(monitor.IsLidOpen);
    }

    [Fact]
    public void SharpDrop_TripsLidOpen()
    {
        var monitor = new LidMonitor();
        FeedStable(monitor, 225, LidMonitor.WindowSize);

        monitor.Update(225 - LidMonitor.DropThresholdF); // exactly the threshold

        Assert.True(monitor.IsLidOpen);
    }

    [Fact]
    public void SmallDrop_DoesNotTrip()
    {
        var monitor = new LidMonitor();
        FeedStable(monitor, 225, LidMonitor.WindowSize);

        monitor.Update(225 - (LidMonitor.DropThresholdF - 5)); // just under threshold

        Assert.False(monitor.IsLidOpen);
    }

    [Fact]
    public void SlowDecline_DoesNotTrip()
    {
        var monitor = new LidMonitor();

        // Lose more than the threshold, but spread slowly across many samples so the
        // drop never appears within a single window — this is a dying fire, not a lid.
        double temp = 225;
        for (int i = 0; i < 100; i++)
        {
            temp -= 1.0; // 1F per tick
            monitor.Update(temp);
            Assert.False(monitor.IsLidOpen);
        }
    }

    [Fact]
    public void WindowNotFull_DoesNotTrip()
    {
        var monitor = new LidMonitor();

        // Fewer than WindowSize samples, with a big drop — should not evaluate yet.
        monitor.Update(225);
        monitor.Update(165);

        Assert.False(monitor.IsLidOpen);
    }

    [Fact]
    public void InvalidTemp_Ignored()
    {
        var monitor = new LidMonitor();
        FeedStable(monitor, 225, LidMonitor.WindowSize);

        monitor.Update(double.NaN);
        monitor.Update(-1);

        Assert.False(monitor.IsLidOpen);
    }

    [Fact]
    public void Recovery_ClearsLatch()
    {
        var monitor = new LidMonitor();
        FeedStable(monitor, 225, LidMonitor.WindowSize);
        monitor.Update(165);
        Assert.True(monitor.IsLidOpen);

        // Temp climbs back to within the recover band of the pre-drop reading (225).
        monitor.Update(225 - LidMonitor.RecoverBandF + 1);

        Assert.False(monitor.IsLidOpen);
    }

    [Fact]
    public void TimeoutFallback_ClearsLatch()
    {
        var monitor = new LidMonitor();
        FeedStable(monitor, 225, LidMonitor.WindowSize);
        monitor.Update(165);
        Assert.True(monitor.IsLidOpen);

        // Stays cold (well below the recover band) for the whole latch budget.
        for (int i = 0; i < LidMonitor.MaxLatchTicks; i++)
        {
            Assert.True(monitor.IsLidOpen);
            monitor.Update(165);
        }

        Assert.False(monitor.IsLidOpen);
    }

    [Fact]
    public void Reset_ClearsLatch()
    {
        var monitor = new LidMonitor();
        FeedStable(monitor, 225, LidMonitor.WindowSize);
        monitor.Update(165);
        Assert.True(monitor.IsLidOpen);

        monitor.Reset();

        Assert.False(monitor.IsLidOpen);
    }
}
