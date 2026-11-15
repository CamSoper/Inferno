using Inferno.Mqtt.Services;

namespace Inferno.Tests
{
    public class UndervoltageTrackerTests
    {
        static readonly DateTimeOffset T0 = new(2026, 11, 15, 16, 0, 0, TimeSpan.FromHours(-6));

        [Fact]
        public void Percent_IsShareOfUndervoltedSamples()
        {
            var t = new UndervoltageTracker(TimeSpan.FromMinutes(10));
            for (int i = 0; i < 10; i++) t.Add(T0.AddSeconds(i * 2), i < 3);

            Assert.Equal(30, t.Percent);
        }

        [Fact]
        public void OldSamplesAgeOut()
        {
            var t = new UndervoltageTracker(TimeSpan.FromMinutes(1));
            t.Add(T0, true);
            t.Add(T0.AddSeconds(30), true);
            t.Add(T0.AddSeconds(90), false);

            // T0 fell out of the window; one of the two remaining was undervolted.
            Assert.Equal(50, t.Percent);
        }

        [Fact]
        public void WindowFull_OnlyAfterMostOfTheWindow()
        {
            var t = new UndervoltageTracker(TimeSpan.FromMinutes(10));
            t.Add(T0, true);
            Assert.False(t.WindowFull(T0.AddMinutes(5)));

            t.Add(T0.AddMinutes(9.5), true);
            Assert.True(t.WindowFull(T0.AddMinutes(9.5)));
        }

        [Fact]
        public void Empty_IsZero()
        {
            Assert.Equal(0, new UndervoltageTracker(TimeSpan.FromMinutes(1)).Percent);
        }
    }
}
