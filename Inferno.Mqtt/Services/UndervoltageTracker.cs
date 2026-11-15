namespace Inferno.Mqtt.Services
{
    /// <summary>
    /// Rolling share of samples that showed undervoltage. The firmware's 4.63V trip
    /// point is fixed in hardware and a marginal supply brushes it constantly, so a
    /// single dip means little. A supply that sags most of the time is the signal.
    /// </summary>
    public sealed class UndervoltageTracker
    {
        private readonly TimeSpan _window;
        private readonly Queue<(DateTimeOffset At, bool Under)> _samples = new();

        public UndervoltageTracker(TimeSpan window)
        {
            _window = window;
        }

        public TimeSpan Window => _window;

        public void Add(DateTimeOffset at, bool undervoltage)
        {
            _samples.Enqueue((at, undervoltage));
            while (_samples.Count > 0 && at - _samples.Peek().At > _window)
            {
                _samples.Dequeue();
            }
        }

        /// <summary>Percent of samples in the window that were undervolted (0-100).</summary>
        public double Percent =>
            _samples.Count == 0 ? 0 : Math.Round(100.0 * _samples.Count(s => s.Under) / _samples.Count, 1);

        /// <summary>True once the window is (nearly) full, so startup can't trip the threshold on a handful of samples.</summary>
        public bool WindowFull(DateTimeOffset now) =>
            _samples.Count > 0 && now - _samples.Peek().At >= _window * 0.9;
    }
}
