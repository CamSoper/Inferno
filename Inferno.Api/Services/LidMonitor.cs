namespace Inferno.Api.Services
{
    /// <summary>
    /// Detects a catastrophic grill-temperature drop — the signature of opening
    /// the lid to load or arrange food — so the controller can ride it out with a
    /// minimal maintenance feed instead of mistaking cold air for a dying fire and
    /// triggering reignition.
    ///
    /// Fed one GrillTemp sample per FireMinder tick (~1 Hz), so the sample window
    /// is effectively a time window, mirroring PreheatMonitor. A lid-open is a
    /// cliff (large drop over a few seconds); a dying fire is a slow slope that
    /// never fills the drop threshold inside the window.
    /// </summary>
    public class LidMonitor
    {
        /// <summary>Number of samples in the rolling window (~10s at the 1 Hz tick rate).</summary>
        public const int WindowSize = 10;

        /// <summary>A drop of at least this many degrees across the window = lid open, not a slow decline.</summary>
        public const double DropThresholdF = 30.0;

        /// <summary>Latch clears once temp climbs back to within this many degrees of the pre-drop reading.</summary>
        public const double RecoverBandF = 20.0;

        /// <summary>Safety cap (~3 min at 1 Hz): never pause normal control indefinitely on a stuck/long-open lid.</summary>
        public const int MaxLatchTicks = 180;

        private readonly Queue<double> _window = new();
        private double _preDropTemp;
        private int _latchTicks;

        public bool IsLidOpen { get; private set; }

        public void Update(double grillTemp)
        {
            if (Double.IsNaN(grillTemp) || grillTemp < 0)
                return;

            if (IsLidOpen)
            {
                _latchTicks++;
                // Lid closed and temp recovered, or we've waited long enough — hand
                // control back to the normal fire-health path either way.
                if (grillTemp >= _preDropTemp - RecoverBandF || _latchTicks >= MaxLatchTicks)
                {
                    Reset();
                    // Seed with the current temp so the refilling window doesn't
                    // immediately re-trip off the now-low readings.
                    _window.Enqueue(grillTemp);
                }
                return;
            }

            _window.Enqueue(grillTemp);
            while (_window.Count > WindowSize)
                _window.Dequeue();

            if (_window.Count < WindowSize)
                return;

            double max = _window.Max();
            if (max - grillTemp >= DropThresholdF)
            {
                IsLidOpen = true;
                _preDropTemp = max;
                _latchTicks = 0;
            }
        }

        public void Reset()
        {
            IsLidOpen = false;
            _latchTicks = 0;
            _window.Clear();
        }
    }
}
