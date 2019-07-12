using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Inferno.Api.Extensions;
using Inferno.Api.Interfaces;
using Inferno.Api.Models;
using Inferno.Api.Pid;

namespace Inferno.Api.Services
{
    public class Smoker : ISmoker
    {
        SmokerMode _mode;
        IAuger _auger;
        IRelayDevice _blower;
        IRelayDevice _igniter;
        IRtdArray _rtdArray;
        IDisplay _display;

        int _setPoint;
        int _maxSetPoint = 450;
        int _minSetPoint = 180;

        TimeSpan _shutdownBlowerTimeout = TimeSpan.FromMinutes(10);
        TimeSpan _holdCycle = TimeSpan.FromSeconds(20);

        CancellationTokenSource _cts;
        SmokerPid _pid;
        DateTime _lastPidUpdate;
        DateTime _lastModeChange;
 
        double _uMax = 1.0;
        double _uMin = 0.15;

        Task _modeLoopTask;
        DisplayUpdater _displayUpdater;
        FireMinder _fireMinder;

        public Smoker(IAuger auger,
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
            _lastModeChange = DateTime.Now;
            PValue = 2;

            CancellationTokenSource _cts = new CancellationTokenSource();

            _pid = new SmokerPid(60.0, 180.0, 45.0);

            _displayUpdater = new DisplayUpdater(this, _display);
            _fireMinder = new FireMinder(this, _igniter);
            _modeLoopTask = ModeLoop();
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
        public int PValue { get; set; }
        public Temps Temps => new Temps()
        {
            GrillTemp = Double.IsNaN(_rtdArray.GrillTemp) ? -1 : _rtdArray.GrillTemp,
            ProbeTemp = Double.IsNaN(_rtdArray.ProbeTemp) ? -1 : _rtdArray.ProbeTemp
        };

        public SmokerStatus Status => new SmokerStatus()
        {
            AugerOn = _auger.IsOn,
            BlowerOn = _blower.IsOn,
            IgniterOn = _igniter.IsOn,
            Temps = this.Temps,
            FireHealthy = _fireMinder.IsFireHealthy,
            Mode = this.Mode.ToString(),
            SetPoint = _setPoint,
            ModeTime = _lastModeChange,
            CurrentTime = DateTime.Now
        };

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

            if (newMode == SmokerMode.Hold)
            {
                _lastPidUpdate = DateTime.Now;
            }

            if (!newMode.IsCookingMode())
            {
                SetPoint = _minSetPoint;
            }

            _mode = newMode;
            _lastModeChange = DateTime.Now;
            if (_cts != null && !_cts.IsCancellationRequested)
            {
                _cts.Cancel();
            }
            return true;
        }

        ///<summary>
        /// Main control loop.
        ///</summary>
        private async Task ModeLoop()
        {
            Debug.WriteLine("Starting mode thread.");
            while (true)
            {
                try
                {
                    using (_cts = new CancellationTokenSource())
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

                            case SmokerMode.Preheat:
                                await Preheat();
                                break;

                            case SmokerMode.Smoke:
                                await Smoke();
                                break;

                            case SmokerMode.Ready:
                                await Ready();
                                break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    string errorText = $"Mode loop exception! {ex} {ex.StackTrace}";
                    Console.WriteLine(errorText);
                    Debug.WriteLine(errorText);
                }

            }
        }

        ///<summary>
        /// Releases pellets at a pre-determined rate for 
        /// low-temperature cooking with lots of smoke.
        ///</summary>
        private async Task Smoke()
        {
            _blower.On();

            await _auger.Run(TimeSpan.FromSeconds(15), _cts.Token);
            if (!_cts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(45 + (10 * PValue)), _cts.Token);
                }
                catch (TaskCanceledException ex)
                {
                    Debug.WriteLine($"{ex} Smoke mode cancelled while waiting.");
                }
            }
            else
            {
                Debug.WriteLine("Smoke mode cancelled while auger was running.");
            }
        }

        ///<summary>
        /// Steady heat driven by PID algorithm.
        ///</summary>
        private async Task Hold()
        {
            _blower.On();

            if (!_fireMinder.IsFireStarted)
            {
                Debug.WriteLine("Hold: Not ignited yet. Diverting to SMOKE mode.");
                await Smoke();
                return;
            }

            if (_pid.SetPoint != _setPoint)
            {
                Debug.WriteLine($"PID setpoint: {_pid.SetPoint}. Actual Setpoint: {SetPoint}. Updating.");
                _pid.SetPoint = _setPoint;
            }

            double u = _pid.GetControlVariable(_rtdArray.GrillTemp).Clamp(_uMin, _uMax);
            TimeSpan runTime = u * _holdCycle;
            await _auger.Run(runTime, _cts.Token);
            if (!_cts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_holdCycle - runTime, _cts.Token);
                }
                catch (TaskCanceledException ex)
                {
                    Debug.WriteLine($"{ex} Hold mode cancelled while waiting.");
                }
            }
            else
            {
                Debug.WriteLine("Hold mode cancelled while auger was running.");
            }
        }


        ///<summary>
        /// Traeger factory algorithm for cooking. 
        /// Generally should not be used. Use Hold instead.
        ///</summary>
        private async Task Preheat()
        {
            _blower.On();

            if (!_fireMinder.IsFireStarted)
            {
                Debug.WriteLine("Preheat: Not ignited yet. Diverting to SMOKE mode.");
                await Smoke();
                return;
            }

            TimeSpan smokeTime = TimeSpan.FromSeconds(15);
            TimeSpan waitTime = TimeSpan.FromSeconds(45 + (10 * PValue));

            if (_rtdArray.GrillTemp >= SetPoint - 2)
            {
                Debug.WriteLine("Preheat: Already at setpoint. - Maintaining.");

                DateTime startTime = DateTime.Now;
                while (DateTime.Now - startTime < smokeTime &&
                        _rtdArray.GrillTemp >= SetPoint - 2)
                {
                    await _auger.Run(TimeSpan.FromSeconds(1), _cts.Token);
                }

                startTime = DateTime.Now;
                while (DateTime.Now - startTime < waitTime &&
                        _rtdArray.GrillTemp >= SetPoint - 2)
                {
                    await (Task.Delay(TimeSpan.FromSeconds(1)));
                }
            }
            else
            {
                await _auger.Run(TimeSpan.FromSeconds(1), _cts.Token);

                if (!_cts.IsCancellationRequested && _rtdArray.GrillTemp >= SetPoint)
                {
                    try
                    {
                        Debug.WriteLine("Setpoint reached while auger was running.");
                        await Task.Delay(waitTime, _cts.Token);
                    }
                    catch (TaskCanceledException ex)
                    {
                        Debug.WriteLine($"{ex} Preheat mode cancelled while waiting to maintain.");
                    }
                }
                else if (_cts.IsCancellationRequested)
                {
                    Debug.WriteLine("Preheat mode cancelled while auger was running.");
                }
            }
        }

        ///<summary>
        /// Turns off everything except for the blower to allow the fire to go out.
        ///</summary>
        private async Task Shutdown()
        {
            _blower.On();
            _igniter.Off();
            try
            {
                await Task.Delay(_shutdownBlowerTimeout, _cts.Token);
                if (_mode == SmokerMode.Error)
                {
                    _blower.Off();
                    Debug.WriteLine("Error mode: Waiting indefinitely for operator to manually set mode to READY.");
                    await Task.Delay(TimeSpan.FromMilliseconds(-1), _cts.Token);
                }
                SetMode(SmokerMode.Ready);
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
            _blower.Off();
            _igniter.Off();
            _fireMinder.ResetFireStatus();

            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }
}