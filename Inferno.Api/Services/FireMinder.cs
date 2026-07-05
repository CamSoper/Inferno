using Inferno.Api.Interfaces;
using Inferno.Common.Extensions;
using Inferno.Common.Interfaces;
using Inferno.Common.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Inferno.Api.Services
{
    public class FireMinder : IDisposable
    {
        ISmoker _smoker;
        IRelayDevice _igniter;
        // Monotonic clock: durations are measured with GetTimestamp/GetElapsedTime so
        // an NTP step (the Pi has no RTC) can't spuriously trip or hang a timeout.
        TimeProvider _timeProvider;
        readonly ILogger<FireMinder> _logger;
        LidMonitor _lidMonitor;
        Task _fireMinderLoop;
        readonly CancellationTokenSource _stopCts = new();
        TimeSpan _igniterTimeout = TimeSpan.FromMinutes(10);
        TimeSpan _fireTimeout = TimeSpan.FromMinutes(10);
        /// <summary>
        /// Consecutive cooking-mode ticks with an invalid grill reading before we
        /// fail safe to Error. RtdArray already debounces ~2s of bad ADC reads before
        /// surfacing NaN, so a few ticks here guards against a lone glitch while still
        /// reacting within seconds to a genuinely dead sensor.
        /// </summary>
        const int SensorFaultTicks = 5;
        int _invalidGrillTicks;
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
        long _igniterOnTimestamp;
        bool _fireCheck;
        long _fireCheckTimestamp;
        long? _belowCheckSince;
        double _recoveryHigh;
        double _ignitionHigh;
        bool _fireStarted;
        int _ignitionTemp;
        /// <summary>
        /// Grill temperature at which the fire was first declared started, captured
        /// once on the initial ignition. Unlike <see cref="_ignitionTemp"/> — which the
        /// recovery path raises to the fire-check temp when relighting a struggling
        /// fire — this stays anchored to where the fire actually caught. That lets Sear
        /// gate its full-feed transition on a margin above the catch temp instead of a
        /// fixed absolute temperature that a cold or windy firepot may never reach.
        /// </summary>
        int _initialIgnitionTemp;
        bool _initialIgnition;

        public bool IsFireHealthy => !_fireCheck;
        public bool IsFireStarted => _fireStarted;
        /// <summary>
        /// Grill temp (F) at which the fire was first declared started. Anchored to the
        /// initial catch — never raised by recovery relights — so callers can derive a
        /// relative establish threshold from it.
        /// </summary>
        public int InitialIgnitionTemp => _initialIgnitionTemp;
        public bool IsReigniting => _fireCheck && _igniter.IsOn;
        // Recovery dominates: never report a lid-open while we're actively recovering,
        // so the Smoker stays on the aggressive RecoveryFeed instead of the floor.
        public bool IsLidOpen => _lidMonitor.IsLidOpen && !_fireCheck;

        public FireMinder(ISmoker smoker, IRelayDevice igniter, TimeProvider? timeProvider = null, bool autoStart = true, ILogger<FireMinder>? logger = null)
        {
            _smoker = smoker;
            _igniter = igniter;
            _timeProvider = timeProvider ?? TimeProvider.System;
            _logger = logger ?? NullLogger<FireMinder>.Instance;
            _lidMonitor = new LidMonitor();
            // Tests drive Tick() directly with a controllable clock; skip the live loop.
            _fireMinderLoop = autoStart ? FireMinderLoop() : Task.CompletedTask;
        }

        public void ResetFireStatus()
        {
            _logger.LogDebug("Resetting fire status.");
            _fireStarted = false;
            _fireCheck = false;
            _initialIgnition = true;
            _ignitionTemp = 150;
            // Conservative default so Sear's relative establish gate stays high until
            // the fire actually catches (and overwrites this with the real catch temp).
            _initialIgnitionTemp = 150;
            _belowCheckSince = null;
            _recoveryHigh = 0;
            _ignitionHigh = 0;
            _invalidGrillTicks = 0;
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
                // Fire-check temp is a fixed fraction of the setpoint: a 30F margin at
                // the 180F floor (150F), scaling proportionally with setpoint. The math
                // is deliberately floating-point — the old integer `SetPoint / 180`
                // collapsed to a step function, putting a ~30F cliff in the threshold
                // at setpoint 360.
                return (int)(_smoker.SetPoint * (150.0 / 180.0));
            }
        }

        private async Task FireMinderLoop()
        {
            _logger.LogDebug("Starting Fire Minder loop.");
            ResetFireStatus();
            while (!_stopCts.IsCancellationRequested)
            {
                try
                {
                    Tick();
                    await Task.Delay(TimeSpan.FromSeconds(1), _stopCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Fire Minder loop exception.");
                    // Back off after a fault so a persistent throw can't hot-spin.
                    try { await Task.Delay(TimeSpan.FromSeconds(1), _stopCts.Token); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }

        public void Dispose()
        {
            _stopCts.Cancel();
            _stopCts.Dispose();
        }

        /// <summary>
        /// One iteration of the fire-health state machine. Extracted from the loop so
        /// it can be driven deterministically in tests with an injected clock.
        /// </summary>
        internal void Tick()
        {
            double currentGrill = _smoker.Temps.GrillTemp;
            bool cooking = _smoker.Mode.IsCookingMode();

            // Sensor-fault fail-safe: a sustained invalid grill reading during a cook
            // (NaN, or the -1 "unplugged" sentinel the Smoker surfaces) means we're
            // managing fire blind. Never drive ignition or the aggressive recovery
            // feed off a garbage temperature — fail safe to Error, where the Smoker
            // cuts fuel and igniter and runs the blower to clear the firepot.
            if (cooking && (double.IsNaN(currentGrill) || currentGrill < 0))
            {
                if (++_invalidGrillTicks >= SensorFaultTicks)
                {
                    _logger.LogError("Grill sensor fault: {Ticks} consecutive invalid readings during cook. Setting error mode.", _invalidGrillTicks);
                    _igniter.Off();
                    _smoker.SetMode(SmokerMode.Error);
                }
                return;
            }
            _invalidGrillTicks = 0;

            // Feed the lid detector during cooking; otherwise keep it clear.
            if (cooking)
            {
                _lidMonitor.Update(currentGrill);
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
                    _igniterOnTimestamp = _timeProvider.GetTimestamp();
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
                    _igniterOnTimestamp = _timeProvider.GetTimestamp();
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
                    _timeProvider.GetElapsedTime(_igniterOnTimestamp) > _igniterTimeout)
            {
                // The igniter has been on for too long, shut it off and go to error mode
                _logger.LogError("Igniter timeout after {Timeout}. Setting error mode.", _igniterTimeout);
                _igniter.Off();
                _smoker.SetMode(SmokerMode.Error);
            }

            if (_smoker.Mode.IsCookingMode())
            {
                if(_smoker.Temps.GrillTemp >= _ignitionTemp)
                {
                    // The fire has started, make sure the igniter is off
                    if (!_fireStarted)
                    {
                        // Capture where the fire caught, once. Recovery may later raise
                        // _ignitionTemp, but this anchor must stay at the initial catch.
                        _initialIgnitionTemp = _ignitionTemp;
                    }
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
                        _fireCheckTimestamp = _timeProvider.GetTimestamp();
                        _igniterOnTimestamp = _timeProvider.GetTimestamp();
                    }
                    else if (_timeProvider.GetElapsedTime(_fireCheckTimestamp) > _fireTimeout)
                    {
                        // No upward progress for the whole timeout — the fire is out.
                        _logger.LogError("Fire timeout: no recovery progress in {Timeout}. Setting error mode.", _fireTimeout);
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
                        _belowCheckSince = _timeProvider.GetTimestamp();
                    }

                    if (_timeProvider.GetElapsedTime(_belowCheckSince.Value) >= _fireCheckDebounce)
                    {
                        // Sustained decline — declare unhealthy and light the igniter
                        // immediately so the recovery feed has an ignition source.
                        _fireCheck = true;
                        _fireCheckTimestamp = _timeProvider.GetTimestamp();
                        _recoveryHigh = grillTemp;
                        if (!_igniter.IsOn)
                        {
                            _igniter.On();
                            _ignitionTemp = Math.Max(150, checkTemp);
                            _igniterOnTimestamp = _timeProvider.GetTimestamp();
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
