using System.Diagnostics;
using Inferno.Api.Interfaces;
using Inferno.Common.Extensions;
using Inferno.Common.Interfaces;
using Inferno.Common.Models;

namespace Inferno.Api.Services
{
    public class FireMinder
    {
        ISmoker _smoker;
        IRelayDevice _igniter;
        Func<DateTime> _now;
        LidMonitor _lidMonitor;
        Task _fireMinderLoop;
        TimeSpan _igniterTimeout = TimeSpan.FromMinutes(10);
        TimeSpan _fireTimeout = TimeSpan.FromMinutes(10);
        /// <summary>
        /// How long the grill must stay continuously below the fire-check temp before
        /// the fire is declared unhealthy. Debounces transient dips (e.g. a quick lid
        /// open) so we don't light the igniter on every blip — a real decline stays
        /// cold for minutes, a lid open recovers in seconds.
        /// </summary>
        TimeSpan _fireCheckDebounce = TimeSpan.FromSeconds(45);
        /// <summary>
        /// How much hotter the grill must get than its best reading since recovery
        /// began to count as "making progress" (and reset the give-up timers). Small
        /// enough to track a real climb, large enough to ignore sensor noise.
        /// </summary>
        const double RecoveryProgressF = 5.0;
        DateTime _igniterOnTime;
        bool _fireCheck;
        DateTime _fireCheckTime;
        DateTime? _belowCheckSince;
        double _recoveryHigh;
        double _ignitionHigh;
        bool _fireStarted;
        int _ignitionTemp;
        bool _initialIgnition;

        public bool IsFireHealthy => !_fireCheck;
        public bool IsFireStarted => _fireStarted;
        public bool IsReigniting => _fireCheck && _igniter.IsOn;
        // Recovery dominates: never report a lid-open while we're actively recovering,
        // so the Smoker stays on the aggressive RecoveryFeed instead of the floor.
        public bool IsLidOpen => _lidMonitor.IsLidOpen && !_fireCheck;

        public FireMinder(ISmoker smoker, IRelayDevice igniter, Func<DateTime>? clock = null, bool autoStart = true)
        {
            _smoker = smoker;
            _igniter = igniter;
            _now = clock ?? (() => DateTime.Now);
            _lidMonitor = new LidMonitor();
            // Tests drive Tick() directly with a controllable clock; skip the live loop.
            _fireMinderLoop = autoStart ? FireMinderLoop() : Task.CompletedTask;
        }

        public void ResetFireStatus()
        {
            Debug.WriteLine("Resetting fire status.");
            _fireStarted = false;
            _fireCheck = false;
            _initialIgnition = true;
            _ignitionTemp = 150;
            _belowCheckSince = null;
            _recoveryHigh = 0;
            _ignitionHigh = 0;
            _lidMonitor.Reset();
        }

        public int GetFireCheckTemp()
        {
            if(_smoker.Mode == SmokerMode.Smoke)
            {
                return 140;
            }
            else
            {
                return _smoker.SetPoint - (_smoker.SetPoint / 180 * 30);
            }
        }

        private async Task FireMinderLoop()
        {
            Debug.WriteLine("Starting Fire Minder thread.");
            ResetFireStatus();
            while (true)
            {
                try
                {
                    Tick();
                    await Task.Delay(TimeSpan.FromSeconds(1));
                }
                catch (Exception ex)
                {
                    string errorText = $"{_now()} Fire Minder loop exception! {ex} {ex.StackTrace}";
                    Console.WriteLine(errorText);
                    Debug.WriteLine(errorText);
                }
            }
        }

        /// <summary>
        /// One iteration of the fire-health state machine. Extracted from the loop so
        /// it can be driven deterministically in tests with an injected clock.
        /// </summary>
        internal void Tick()
        {
            // Feed the lid detector during cooking; otherwise keep it clear.
            if (_smoker.Mode.IsCookingMode())
            {
                _lidMonitor.Update(_smoker.Temps.GrillTemp);
            }
            else
            {
                _lidMonitor.Reset();
            }

            if (_smoker.Mode.IsCookingMode() &&
                _smoker.Temps.GrillTemp < _ignitionTemp &&
                !_fireStarted)
            {
                double grillTemp = _smoker.Temps.GrillTemp;
                if (!_igniter.IsOn)
                {
                    // The fire is not started, turn on the igniter
                    _igniter.On();
                    _ignitionTemp = Math.Max(_ignitionTemp, Convert.ToInt32(grillTemp) + 10);
                    _igniterOnTime = _now();
                    _ignitionHigh = grillTemp;
                }
                else if (grillTemp > _ignitionHigh + RecoveryProgressF)
                {
                    // The grill is climbing toward ignition — the fire is catching,
                    // even if slowly. Reset the igniter give-up clock so a cold or
                    // slow start isn't killed by the fixed deadline, mirroring the
                    // recovery path below. A truly dead light (no temperature rise)
                    // makes no progress and still times out.
                    _ignitionHigh = grillTemp;
                    _igniterOnTime = _now();
                }
            }

            if (_smoker.Mode.IsCookingMode() &&
                _smoker.Temps.GrillTemp > GetFireCheckTemp() &&
                _fireStarted &&
                _initialIgnition)
            {
                // The fire has been lit at least once.
                _initialIgnition = false;
            }

            if (_igniter.IsOn &&
                    _now() - _igniterOnTime > _igniterTimeout)
            {
                // The igniter has been on for too long, shut it off and go to error mode
                string errorText = $"{_now()} Igniter timeout. Setting error mode.";
                Debug.WriteLine(errorText);
                Console.WriteLine(errorText);
                _igniter.Off();
                _smoker.SetMode(SmokerMode.Error);
            }

            if (_smoker.Mode.IsCookingMode())
            {
                if(_smoker.Temps.GrillTemp >= _ignitionTemp)
                {
                    // The fire has started, make sure the igniter is off
                    _fireStarted = true;
                    _igniter.Off();
                }

                bool established = _fireStarted && !_initialIgnition;
                double grillTemp = _smoker.Temps.GrillTemp;
                int checkTemp = GetFireCheckTemp();

                if (!established)
                {
                    // Still in initial startup; nothing to monitor yet.
                }
                else if (_fireCheck)
                {
                    // Already in recovery: the igniter is lit and the Smoker is
                    // running an aggressive recovery feed.
                    if (grillTemp >= checkTemp)
                    {
                        // The fire is healthy again.
                        _igniter.Off();
                        _fireCheck = false;
                        _belowCheckSince = null;
                    }
                    else if (grillTemp > _recoveryHigh + RecoveryProgressF)
                    {
                        // The fire is climbing — recovery is working. Restart the
                        // give-up clocks (both the fire timeout and the igniter
                        // timeout) so a slow-but-steady recovery isn't killed by a
                        // fixed deadline.
                        _recoveryHigh = grillTemp;
                        _fireCheckTime = _now();
                        _igniterOnTime = _now();
                    }
                    else if (_now() - _fireCheckTime > _fireTimeout)
                    {
                        // No upward progress for the whole timeout — the fire is out.
                        string errorText = $"{_now()} Fire timeout. Setting error mode.";
                        Debug.WriteLine(errorText);
                        Console.WriteLine(errorText);
                        _smoker.SetMode(SmokerMode.Error);
                    }
                }
                else if (_lidMonitor.IsLidOpen)
                {
                    // A catastrophic drop means the lid is open — cold air, not a dying
                    // fire. Don't start recovery; reset the debounce so a real decline
                    // after the lid closes still has to prove itself.
                    _belowCheckSince = null;
                }
                else if (grillTemp < checkTemp)
                {
                    // The fire might be going out. Require it to stay low for the
                    // debounce window before declaring it unhealthy.
                    if (_belowCheckSince == null)
                    {
                        _belowCheckSince = _now();
                    }

                    if (_now() - _belowCheckSince >= _fireCheckDebounce)
                    {
                        // Sustained decline — declare unhealthy and light the igniter
                        // immediately so the recovery feed has an ignition source.
                        _fireCheck = true;
                        _fireCheckTime = _now();
                        _recoveryHigh = grillTemp;
                        if (!_igniter.IsOn)
                        {
                            _igniter.On();
                            _ignitionTemp = Math.Max(150, checkTemp);
                            _igniterOnTime = _now();
                        }
                    }
                }
                else
                {
                    // Healthy: at or above the check temp.
                    _belowCheckSince = null;
                }
            }
        }
    }
}
