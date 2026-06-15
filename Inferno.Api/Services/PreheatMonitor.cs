namespace Inferno.Api.Services
{
    public class PreheatMonitor
    {
        public const int WindowSize = 60;
        public const double MaxRange = 15.0;
        public const double ProximityPct = 0.10;

        private readonly Queue<double> _tempHistory = new();
        // Update() runs on a fixed-cadence loop while Reset() is driven from API
        // threads (mode changes); guard the (non-concurrent) queue against both.
        private readonly object _lock = new();

        public bool IsPreheated { get; private set; }

        public void Update(double grillTemp, int setPoint, bool isCookingMode, bool isFireHealthy)
        {
            lock (_lock)
            {
                if (IsPreheated) return;

                if (!isCookingMode || !isFireHealthy)
                {
                    _tempHistory.Clear();
                    return;
                }

                if (Double.IsNaN(grillTemp) || grillTemp < 0)
                    return;

                _tempHistory.Enqueue(grillTemp);
                while (_tempHistory.Count > WindowSize)
                    _tempHistory.Dequeue();

                if (_tempHistory.Count < WindowSize)
                    return;

                double min = _tempHistory.Min();
                double max = _tempHistory.Max();
                if (max - min >= MaxRange)
                    return;

                double avg = _tempHistory.Average();
                double threshold = setPoint * (1.0 - ProximityPct);
                if (avg < threshold)
                    return;

                IsPreheated = true;
            }
        }

        public void Reset()
        {
            lock (_lock)
            {
                IsPreheated = false;
                _tempHistory.Clear();
            }
        }
    }
}
