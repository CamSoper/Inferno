using System.Diagnostics;
using Inferno.Api.Interfaces;
using Inferno.Common.Interfaces;
using Inferno.Common.Models;
using Inferno.Api.Pid;
using Inferno.Common.Extensions;

namespace Inferno.Api.Services
{
    public class Smoker : ISmoker, IDisposable
    {
        SmokerMode _mode;
        IRelayDevice _auger;
        IRelayDevice _blower;
        IRelayDevice _igniter;
        IRtdArray _rtdArray;
        IDisplay _display;

        int _setPoint;
        /// <summary>
        /// Arbitrary factor to determine how much to run the auger each cycle in Smoke mode.
        /// Borrowed from Traeger's "P" setting. 1 is the lowest, 5 is the highest.
        /// </summary>
        int _pValue;
        int _maxSetPoint = 400;
        int _minSetPoint = 180;

        int _maxGrillTemp = 425;

        /// <summary>
        /// Timeout for the blower to run after shutdown.
        /// </summary>
        TimeSpan _shutdownBlowerTimeout = TimeSpan.FromMinutes(10);
        
        /// <summary>
        /// Hold cycle time. This is the amount of time a Hold iteration takes in total.
        /// The PID determines a period of time to run the auger as a percentage of this time.
        /// Also used in Sear mode to determine how long to run the auger when the grill is too hot.
        /// </summary>
        TimeSpan _holdCycle = TimeSpan.FromSeconds(10);

        // Per-mode token: cancelled by SetMode to interrupt the running mode's delay.
        // Guarded by _ctsLock so SetMode's Cancel() can never race ModeLoop's Dispose().
        CancellationTokenSource _cts = null!;
        readonly object _ctsLock = new();
        // Cancelled once, on Dispose, to stop every background loop for a clean shutdown.
        readonly CancellationTokenSource _lifetimeCts = new();
        SmokerPid _pid;
        DateTime _lastModeChange;
 
        /// <summary>
        /// Maximum value for the PID output. This is the maximum amount of the "hold" cycle time that the auger will run.
        /// </summary>
        double _uMax = 1.0;
        /// <summary>
        /// Minimum value for the PID output. This is the minimum amount of the "hold" cycle time that the auger will run.
        /// Too low of a value can cause the fire to go out. Too high of a value can result in the fire being too hot.
        /// </summary>
        double _uMin = 0.175;

        /// <summary>
        /// Fire-sustaining floor feed, used to ride out a lid-open temperature drop.
        /// Keeps fuel on the fire without over-feeding an open, oxygen-rich firepot
        /// (which would flare and overshoot when the lid closes). Never zero — that
        /// would starve the fire. ~17% duty, in line with the PID floor (_uMin).
        /// No recovery path may ever feed below this; the original starvation bug fed
        /// ~5%, well under it.
        /// </summary>
        TimeSpan _maintenanceFeedRunTime = TimeSpan.FromSeconds(5);
        TimeSpan _maintenanceFeedWaitTime = TimeSpan.FromSeconds(25);

        /// <summary>
        /// Aggressive feed to recover a genuinely struggling fire. FireMinder has the
        /// igniter lit during recovery, so pellets light as they land. High duty with
        /// a short off-gap (~75%): rebuilds the coal bed fast, but the gap lets each
        /// charge catch before the next so fuel doesn't pile up and smother the embers.
        /// </summary>
        TimeSpan _recoveryFeedRunTime = TimeSpan.FromSeconds(15);
        TimeSpan _recoveryFeedWaitTime = TimeSpan.FromSeconds(5);

        Task _modeLoopTask;
        Task _preheatLoopTask;
        DisplayUpdater _displayUpdater;
        FireMinder _fireMinder;
        PreheatMonitor _preheatMonitor;

        public Smoker(IRelayDevice auger,
                        IRelayDevice blower,
                        IRelayDevice igniter,
                        IRtdArray rtdArray,
                        IDisplay display)
        {
            _auger = auger;
            _blower = blower;
            _igniter = igniter;
            _rtdArray = rtdArray;
            _display = display;

            _mode = SmokerMode.Ready;
            _setPoint = _minSetPoint;
            _lastModeChange = DateTime.Now;
            PValue = 2;

            _pid = new SmokerPid(60.0, 180.0, 45.0);

            _displayUpdater = new DisplayUpdater(this, _display);
            _fireMinder = new FireMinder(this, _igniter);
            _preheatMonitor = new PreheatMonitor();
            _modeLoopTask = ModeLoop();
            _preheatLoopTask = PreheatLoop();
        }

        public SmokerMode Mode => _mode;

        public int SetPoint
        {
            get
            {
                return _setPoint;
            }
            set
            {
                _setPoint = value.Clamp(_minSetPoint, _maxSetPoint);
            }
        }
        public int PValue
        {
            get => _pValue;            
            set
            {
                _pValue = value.Clamp(0, 5);
            }
        }
        public Temps Temps => new Temps()
        {
            GrillTemp = Double.IsNaN(_rtdArray.GrillTemp) ? -1 : _rtdArray.GrillTemp,
            ProbeTemp = Double.IsNaN(_rtdArray.ProbeTemp) ? -1 : _rtdArray.ProbeTemp
        };

        public SmokerStatus Status
        {
            get
            {
                // Preheat sampling runs on its own fixed-cadence loop (PreheatLoop);
                // reading status no longer mutates it. This keeps the 60-sample window
                // a true ~60s and avoids a data race on the monitor's queue.
                return new SmokerStatus()
                {
                    AugerOn = _auger.IsOn,
                    BlowerOn = _blower.IsOn,
                    IgniterOn = _igniter.IsOn,
                    Temps = this.Temps,
                    FireHealthy = _fireMinder.IsFireHealthy,
                    Preheated = _preheatMonitor.IsPreheated,
                    Mode = this.Mode.ToString(),
                    SetPoint = _setPoint,
                    PValue = _pValue,
                    ModeTime = _lastModeChange,
                    CurrentTime = DateTime.Now
                };
            }
        }
        
        public bool SetMode(SmokerMode newMode)
        {
            Debug.WriteLine($"Setting mode {newMode}.");

            SmokerMode currentMode = _mode;

            if (newMode == currentMode)
            {
                return true;
            }

            if (newMode == SmokerMode.Ready &&
                currentMode.IsCookingMode())
            {
                return false;
            }

            if (newMode.IsCookingMode() && 
                currentMode == SmokerMode.Shutdown)
            {
                return false;
            }

            if (newMode.IsCookingMode() &&
                currentMode == SmokerMode.Ready)
            {
                _fireMinder.ResetFireStatus();
            }

            if (newMode == SmokerMode.Smoke)
            {
                _setPoint = _minSetPoint;
            }

            if (newMode == SmokerMode.Sear)
            {
                _setPoint = _maxSetPoint;
            }

            if (!newMode.IsCookingMode())
            {
                SetPoint = _minSetPoint;
            }

            if (newMode == SmokerMode.Ready || newMode == SmokerMode.Shutdown)
            {
                _preheatMonitor.Reset();
            }

            _mode = newMode;
            _lastModeChange = DateTime.Now;
            // Interrupt the running mode's in-flight delay. ModeLoop owns disposal of
            // the token (under the same lock), so cancelling here is always safe.
            lock (_ctsLock)
            {
                if (_cts != null && !_cts.IsCancellationRequested)
                {
                    _cts.Cancel();
                }
            }
            return true;
        }

        ///<summary>
        /// Main control loop.
        ///</summary>
        private async Task ModeLoop()
        {
            Debug.WriteLine("Starting mode thread.");
            while (!_lifetimeCts.IsCancellationRequested)
            {
                // Fresh per-mode token, linked to the lifetime token so Dispose() also
                // unblocks the running mode. The previous iteration's token is disposed
                // here (its awaits have all completed) under the lock SetMode uses to
                // cancel, so Cancel() and Dispose() can never race.
                lock (_ctsLock)
                {
                    _cts?.Dispose();
                    _cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
                }

                try
                {
                    switch (_mode)
                    {
                        case SmokerMode.Error:
                        case SmokerMode.Shutdown:
                            await Shutdown();
                            break;

                        case SmokerMode.Hold:
                            await Hold();
                            break;

                        case SmokerMode.Sear:
                            await Sear();
                            break;

                        case SmokerMode.Smoke:
                            await Smoke();
                            break;

                        case SmokerMode.Ready:
                            await Ready();
                            break;
                    }
                }
                catch (Exception ex)
                {
                    string errorText = $"{DateTime.Now} Mode loop exception! {ex} {ex.StackTrace}";
                    Console.WriteLine(errorText);
                    Debug.WriteLine(errorText);
                }

            }
        }

        ///<summary>
        /// Samples the preheat detector on a fixed 1 Hz cadence. Kept off the Status
        /// getter so the rolling window stays a true ~60s regardless of how many
        /// clients poll status, and so the (non-concurrent) window isn't raced.
        ///</summary>
        private async Task PreheatLoop()
        {
            while (!_lifetimeCts.IsCancellationRequested)
            {
                try
                {
                    _preheatMonitor.Update(
                        _rtdArray.GrillTemp, _setPoint,
                        _mode.IsCookingMode(), _fireMinder.IsFireHealthy);
                    await Task.Delay(TimeSpan.FromSeconds(1), _lifetimeCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"{DateTime.Now} Preheat loop exception! {ex.Message}");
                }
            }
        }

        ///<summary>
        /// Releases pellets at a pre-determined rate for 
        /// low-temperature cooking with lots of smoke.
        ///</summary>
        private async Task Smoke()
        {
            if (_fireMinder.IsLidOpen)
            {
                // Lid open: ride out the temp drop on a maintenance feed and leave the
                // PID untouched (returning here freezes it — no integral windup, no
                // overshoot when the lid closes).
                await MaintenanceFeed();
                return;
            }

            if (_fireMinder.IsReigniting)
            {
                await RecoveryFeed();
                return;
            }

            _blower.On();

            TimeSpan waitTime = TimeSpan.FromSeconds(45 + (10 * PValue));
            await RunAuger(TimeSpan.FromSeconds(15), waitTime);
            if (_cts.IsCancellationRequested)
            {
                Debug.WriteLine("Smoke mode cancelled.");
            }
        }

        ///<summary>
        /// Steady heat driven by PID algorithm.
        ///</summary>
        private async Task Hold()
        {
            if (_fireMinder.IsLidOpen)
            {
                // Lid open: ride out the temp drop on a maintenance feed and leave the
                // PID untouched (returning here freezes it — no integral windup, no
                // overshoot when the lid closes).
                await MaintenanceFeed();
                return;
            }

            if (_fireMinder.IsReigniting)
            {
                await RecoveryFeed();
                return;
            }

            _blower.On();

            if (_igniter.IsOn && !_fireMinder.IsFireStarted)
            {
               Debug.WriteLine("Hold: Igniter is on during startup. Diverting to SMOKE mode.");
               await Smoke();
               return;
            }

            if (_setPoint == _maxSetPoint && _rtdArray.GrillTemp < _setPoint)
            {
                Debug.WriteLine("Hold: Max setting. Skipping the PID, just running the auger.");
                await RunAuger();
                return;
            }

            if (_pid.SetPoint != _setPoint)
            {
                Debug.WriteLine($"PID setpoint: {_pid.SetPoint}. Actual Setpoint: {SetPoint}. Updating.");
                _pid.SetPoint = _setPoint;
            }

            double u = _pid.GetControlVariable(_rtdArray.GrillTemp).Clamp(_uMin, _uMax);
            if(double.IsNaN(u))
            {
                Debug.WriteLine($"Hold: PID returned NaN. Setting u to {_uMin}.");
                u = _uMin;
            }
            
            TimeSpan runTime = u * _holdCycle;
            if (runTime == _holdCycle)
            {
                await RunAuger();
            }
            else
            {
                // Run a certain amount of time
                await RunAuger(runTime, _holdCycle - runTime);
            }
        }

        private async Task RunAuger(TimeSpan RunTime, TimeSpan WaitTime)
        {
            Debug.WriteLine($"Auger running: {RunTime.Seconds} seconds.");
            // Run the auger
            _auger.On();
            try
            {
                await Task.Delay(RunTime, _cts.Token);
            }
            catch (TaskCanceledException ex)
            {
                Debug.WriteLine($"{ex} Cancelled while auger running.");
                return;
            }

            _auger.Off();
            try
            {
                await Task.Delay(WaitTime, _cts.Token);
            }
            catch (TaskCanceledException ex)
            {
                Debug.WriteLine($"{ex} Cancelled while auger waiting.");
            }
        }

        private async Task RunAuger()
        {
            // Run the entire runtime unless we hear otherwise
            _auger.On();
            try
            {
                await Task.Delay(_holdCycle, _cts.Token);
            }
            catch (TaskCanceledException ex)
            {
                Debug.WriteLine($"{ex} Running auger cancelled.");
            }
        }


        ///<summary>
        /// Aggressive feed to recover a struggling fire. FireMinder lights the igniter
        /// the moment the fire is declared unhealthy, so pellets ignite as they land
        /// instead of accumulating. This rebuilds the coal bed rather than starving it.
        ///</summary>
        private async Task RecoveryFeed()
        {
            _blower.On();
            Debug.WriteLine("Recovery feed: aggressive auger to rebuild the fire.");
            await RunAuger(_recoveryFeedRunTime, _recoveryFeedWaitTime);
        }

        ///<summary>
        /// Minimal fire-sustaining feed while riding out a lid-open temperature drop.
        /// Keeps fuel on the fire without over-feeding an open, oxygen-rich firepot.
        /// Never zero — that would starve the fire.
        ///</summary>
        private async Task MaintenanceFeed()
        {
            _blower.On();
            Debug.WriteLine("Maintenance feed: lid open, sustaining the fire.");
            await RunAuger(_maintenanceFeedRunTime, _maintenanceFeedWaitTime);
        }

        ///<summary>
        /// Burn hot
        ///</summary>
        private async Task Sear()
        {
            if (_fireMinder.IsLidOpen)
            {
                // Lid open: ride out the temp drop on a maintenance feed and leave the
                // PID untouched (returning here freezes it — no integral windup, no
                // overshoot when the lid closes).
                await MaintenanceFeed();
                return;
            }

            if (_fireMinder.IsReigniting)
            {
                await RecoveryFeed();
                return;
            }

            if (_igniter.IsOn && !_fireMinder.IsFireStarted)
            {
               Debug.WriteLine("Sear: Igniter is on during startup. Diverting to SMOKE mode.");
               await Smoke();
               return;
            }

            if (_rtdArray.GrillTemp < _minSetPoint)
            {
                Debug.WriteLine($"Sear: Grill temp {_rtdArray.GrillTemp} below {_minSetPoint}. Diverting to SMOKE to establish fire.");
                await Smoke();
                return;
            }

            if (_rtdArray.GrillTemp < _maxGrillTemp)
            {
                await RunAuger();
            }
            else
            {
                Debug.WriteLine($"Sear: Over max grill temp. Running minimum auger time.");
                var runTime = _holdCycle * _uMin;
                await RunAuger(runTime, _holdCycle - runTime);
            }
        }

        ///<summary>
        /// Turns off everything except for the blower to allow the fire to go out.
        ///</summary>
        private async Task Shutdown()
        {
            _auger.Off();
            _blower.On();
            _igniter.Off();
            try
            {
                if (DateTime.Now - _lastModeChange < _shutdownBlowerTimeout)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), _cts.Token);
                }
                else
                {
                    SetMode(SmokerMode.Ready);
                }
            }
            catch (TaskCanceledException ex)
            {
                Debug.WriteLine($"{ex} Shutdown mode cancelled.");
            }
        }

        ///<summary>
        /// Ready to cook.
        ///</summary>
        private async Task Ready()
        {
            _auger.Off();
            _blower.Off();
            _igniter.Off();

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _cts.Token);
            }
            catch (TaskCanceledException)
            {
            }
        }

        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Stop every background loop first so nothing re-energizes a relay while
            // we're tearing down.
            _lifetimeCts.Cancel();
            _fireMinder?.Dispose();
            _displayUpdater?.Dispose();

            // Drive the fire to a safe terminal state: cut fuel and ignition. Process
            // shutdown can't block for the timed Shutdown-mode cooldown (systemd would
            // SIGKILL us), so this is a hard safe-off — residual heat dissipates on its
            // own. The blower is released below.
            try { _auger.Off(); } catch { }
            try { _igniter.Off(); } catch { }

            // Release hardware. RelayDevice.Dispose drives the pin off and closes it;
            // RtdArray stops its read loop and frees the ADC/SPI; Display frees the LCD.
            (_auger as IDisposable)?.Dispose();
            (_blower as IDisposable)?.Dispose();
            (_igniter as IDisposable)?.Dispose();
            (_rtdArray as IDisposable)?.Dispose();
            (_display as IDisposable)?.Dispose();

            lock (_ctsLock)
            {
                _cts?.Dispose();
            }
            // Deliberately not disposing _lifetimeCts: ModeLoop may still read its Token
            // as it winds down, and disposing it would throw. The process is exiting, so
            // the single lingering CancellationTokenSource is reclaimed anyway.
        }
    }
}