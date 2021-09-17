using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Inferno.Api.Interfaces;
using Inferno.Common.Models;
using Inferno.Api.Pid;
using Inferno.Common.Extensions;

namespace Inferno.Api.Services
{
    public class Smoker : ISmoker
    {
        SmokerMode _mode;
        IRelayDevice _auger;
        IRelayDevice _blower;
        IRelayDevice _igniter;
        IRtdArray _rtdArray;
        IDisplay _display;

        int _setPoint;
        int _pValue;
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
        /// Traeger factory algorithm for cooking. 
        /// Generally should not be used. Use Hold instead.
        ///</summary>
        private Task Preheat()
        {
            throw new NotImplementedException();
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
            _auger.Off();
            _blower.Off();
            _igniter.Off();

            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }
}