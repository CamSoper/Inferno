using System.Diagnostics;
using Inferno.Api.Interfaces;
using Inferno.Api.Models;
using Inferno.Common.Extensions;
using Inferno.Common.Interfaces;
using Inferno.Common.Models;

namespace Inferno.Api.Services
{
    /// <summary>
    /// Records cook history by polling <see cref="ISmoker.Status"/> on a fixed cadence,
    /// inferring cook-session boundaries from mode transitions (see <see cref="Tick"/>).
    /// It deliberately does NOT hook into the safety-critical state machine: a logging
    /// failure stays contained here and can never stall a mode change or fire control.
    /// Mirrors the loop+Tick pattern of <see cref="DisplayUpdater"/>/FireMinder.
    /// </summary>
    public class CookLogger : IDisposable
    {
        // ~10s matches the Hold cycle; one sample per cycle is plenty of resolution
        // for history graphs without churning the SD card.
        static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(10);

        readonly ISmoker _smoker;
        readonly ICookLogStore _store;
        readonly Func<DateTime> _now;
        readonly int _flushThreshold;
        readonly List<CookSample> _buffer = new();
        readonly CancellationTokenSource _stopCts = new();
        readonly Task _loop;

        SmokerMode _previousMode;
        long? _currentSessionId;
        double _peakGrill;
        double _peakProbe;
        int _sampleCount;

        public CookLogger(ISmoker smoker, ICookLogStore store, Func<DateTime>? clock = null,
            bool autoStart = true, int flushThreshold = 6)
        {
            _smoker = smoker;
            _store = store;
            _now = clock ?? (() => DateTime.Now);
            _flushThreshold = flushThreshold;

            ResumeOrReset();

            // Tests drive Tick() directly; skip the live loop.
            _loop = autoStart ? LoggerLoop() : Task.CompletedTask;
        }

        /// <summary>
        /// On startup, reconcile with any session left open by a previous run:
        /// resume it if we're still cooking (API restarted mid-cook), or close out an
        /// orphan from an unclean shutdown. Otherwise start from a clean baseline so the
        /// first Tick opens a session if the smoker is already cooking.
        /// </summary>
        void ResumeOrReset()
        {
            long? active = _store.GetActiveSessionId();
            if (active != null && _smoker.Mode.IsCookingMode())
            {
                _currentSessionId = active;
                _previousMode = _smoker.Mode;
                _peakGrill = double.NaN;
                _peakProbe = double.NaN;
                _sampleCount = 0;
            }
            else
            {
                if (active != null)
                {
                    _store.CloseSession(active.Value, _now(), double.NaN, double.NaN, 0);
                }
                _previousMode = SmokerMode.Ready;
            }
        }

        async Task LoggerLoop()
        {
            Debug.WriteLine("Starting cook logger thread.");
            while (!_stopCts.IsCancellationRequested)
            {
                try
                {
                    Tick();
                    await Task.Delay(SampleInterval, _stopCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"{_now()} Cook logger loop exception! {ex.Message}");
                }
            }
        }

        /// <summary>
        /// One sampling iteration. Detects session boundaries from the mode transition,
        /// then (if a session is open) buffers a sample and flushes on the batch threshold.
        /// Extracted from the loop so tests can drive it deterministically.
        /// </summary>
        internal void Tick()
        {
            var status = _smoker.Status;
            var mode = _smoker.Mode;

            // Open on first entry into a cooking mode from a non-cooking one.
            if (!_previousMode.IsCookingMode() && mode.IsCookingMode() && _currentSessionId == null)
            {
                OpenSession();
            }
            // Close once the smoker returns to Ready (after the Shutdown cooldown, whose
            // samples we keep in this same session).
            else if (_currentSessionId != null && mode == SmokerMode.Ready)
            {
                CloseSession();
            }

            _previousMode = mode;

            if (_currentSessionId == null)
            {
                return;
            }

            _buffer.Add(new CookSample
            {
                Timestamp = _now(),
                GrillTemp = status.Temps?.GrillTemp ?? double.NaN,
                ProbeTemp = status.Temps?.ProbeTemp ?? double.NaN,
                Mode = status.Mode,
                SetPoint = status.SetPoint,
                PValue = status.PValue,
                AugerOn = status.AugerOn,
                BlowerOn = status.BlowerOn,
                IgniterOn = status.IgniterOn,
                FireHealthy = status.FireHealthy,
                Preheated = status.Preheated,
            });
            _sampleCount++;
            TrackPeaks(status.Temps);

            if (_buffer.Count >= _flushThreshold)
            {
                Flush();
            }
        }

        void OpenSession()
        {
            _currentSessionId = _store.OpenSession(_now(), null);
            _peakGrill = double.NaN;
            _peakProbe = double.NaN;
            _sampleCount = 0;
            _buffer.Clear();
        }

        void CloseSession()
        {
            if (_currentSessionId == null)
            {
                return;
            }

            Flush();
            _store.CloseSession(_currentSessionId.Value, _now(), _peakGrill, _peakProbe, _sampleCount);
            _currentSessionId = null;
        }

        void Flush()
        {
            if (_currentSessionId == null || _buffer.Count == 0)
            {
                return;
            }

            _store.InsertSamples(_currentSessionId.Value, _buffer);
            _buffer.Clear();
        }

        void TrackPeaks(Temps? temps)
        {
            if (temps == null)
            {
                return;
            }

            if (double.IsNaN(_peakGrill) || temps.GrillTemp > _peakGrill)
            {
                _peakGrill = temps.GrillTemp;
            }
            if (double.IsNaN(_peakProbe) || temps.ProbeTemp > _peakProbe)
            {
                _peakProbe = temps.ProbeTemp;
            }
        }

        public void Dispose()
        {
            _stopCts.Cancel();
            try
            {
                // Flush buffered samples and close out an in-progress cook so a clean
                // restart doesn't leave a dangling open session.
                if (_currentSessionId != null)
                {
                    CloseSession();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"{_now()} Cook logger dispose flush failed: {ex.Message}");
            }
            _stopCts.Dispose();
        }
    }
}
