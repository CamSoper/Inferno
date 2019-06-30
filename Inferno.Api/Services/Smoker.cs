using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Inferno.Api.Algorithms;
using Inferno.Api.Interfaces;
using Inferno.Api.Models;

namespace Inferno.Api.Services
{
    public class Smoker : ISmoker
    {
        SmokerMode _mode;
        IAuger _auger;
        IBlower _blower;
        IIgniter _igniter;
        ITempArray _tempArray;
        IDisplay _display;

        int _maxSetPoint = 450;
        int _minSetPoint = 180;
        int _ignitionTemp = 140;

        TimeSpan _shutdownBlowerTimeout = TimeSpan.FromMinutes(10);
        TimeSpan _igniterTimeout = TimeSpan.FromMinutes(10);
        DateTime _igniterOnTime;
        TimeSpan _holdCycle = TimeSpan.FromSeconds(20);

        CancellationTokenSource _cts;
        PidController _pid;
        DateTime _lastPidUpdate;

        double _uMax = 1.0;
        double _uMin = 0.15;

        bool _fireStarted;
        Task _modeTask;
        Task _displayTask;
        Task _igniterTask;

        public Smoker(IAuger auger,
                        IBlower blower,
                        IIgniter igniter,
                        ITempArray tempArray,
                        IDisplay display)
        {
            _auger = auger;
            _blower = blower;
            _igniter = igniter;
            _tempArray = tempArray;
            _display = display;

            _mode = SmokerMode.Ready;
            SetPoint = _minSetPoint;
            PValue = 2;

            CancellationTokenSource _cts = new CancellationTokenSource();

            _pid = new PidController(60.0, 180.0, 45.0);

            _fireStarted = false;
            _displayTask = UpdateDisplay();
            _modeTask = ModeLoop();
            _igniterTask = StartFire();
        }

        public SmokerMode Mode => _mode;
        public int SetPoint { get; set; }
        public int PValue { get; set; }
        public Temps Temps => new Temps()
        {
            GrillTemp = _tempArray.GrillTemp,
            ProbeTemp = Double.IsNaN(_tempArray.ProbeTemp) ? -1 : _tempArray.ProbeTemp
        };

        public bool SetMode(SmokerMode mode)
        {
            Debug.WriteLine($"Setting mode {mode}.");

            SmokerMode currentMode = _mode;

            // Must run Shutdown or Error before switching to Ready
            if (mode == SmokerMode.Ready &&
                currentMode != SmokerMode.Shutdown &&
                currentMode != SmokerMode.Error)
            {
                return false;
            }

            // Can't go to Shutdown from Ready
            if (currentMode == SmokerMode.Ready &&
                 mode == SmokerMode.Shutdown)
            {
                return false;
            }

            // Init _lastPidUpdate if switching to HOLD
            if (mode == SmokerMode.Hold)
            {
                _lastPidUpdate = DateTime.Now;
            }

            _mode = mode;
            if (_cts != null && !_cts.IsCancellationRequested)
            {
                _cts.Cancel();
            }
            return true;
        }

        private async Task UpdateDisplay()
        {
            Debug.WriteLine("Starting display thread.");
            while (true)
            {
                switch (_mode)
                {
                    case SmokerMode.Ready:
                        _display.DisplayText(DateTime.Now.ToShortDateString().PadLeft(20),
                            DateTime.Now.ToShortTimeString().PadLeft(20),
                            new string('-', 20),
                            "Ready");
                        break;

                    case SmokerMode.Shutdown:
                        _display.DisplayInfo(_tempArray.GrillTemp, _tempArray.ProbeTemp, "Shutting Down", HardwareStatus());
                        break;

                    case SmokerMode.Hold:
                        _display.DisplayInfo(_tempArray.GrillTemp, _tempArray.ProbeTemp, $"Hold {SetPoint}*F", HardwareStatus());
                        break;

                    case SmokerMode.Preheat:
                        _display.DisplayInfo(_tempArray.GrillTemp, _tempArray.ProbeTemp, $"Preheat {SetPoint}*F", HardwareStatus());
                        break;

                    case SmokerMode.Smoke:
                        _display.DisplayInfo(_tempArray.GrillTemp, _tempArray.ProbeTemp, $"Smoke P-{PValue}", HardwareStatus());
                        break;

                    case SmokerMode.Error:
                        _display.DisplayInfo(_tempArray.GrillTemp, _tempArray.ProbeTemp, $"Error:Clear fire pot", "");
                        break;
                }

                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }

        private string HardwareStatus()
        {
            string igniter = (_igniter.IsOn) ? "I" : " ";
            string auger = (_auger.IsOn) ? "A" : " ";

            return $"{igniter}{auger}";
        }

        private async Task ModeLoop()
        {
            Debug.WriteLine("Starting mode thread.");
            while (true)
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

                        case SmokerMode.Smoke:
                            await Smoke();
                            break;

                        case SmokerMode.Preheat:
                            await Preheat();
                            break;

                        case SmokerMode.Ready:
                            await Ready();
                            break;
                    }
                }
            }
        }

        private async Task Preheat()
        {
            _blower.On();
            
            if (_tempArray.GrillTemp < _ignitionTemp)
            {
                Debug.WriteLine("Preheat: Not ignited yet - Diverting to SMOKE mode.");
                await Smoke();
                return;
            }

            if (_tempArray.GrillTemp < SetPoint - 10)
            {
                await _auger.Run(TimeSpan.FromSeconds(10), _cts.Token);
                if (_cts.IsCancellationRequested)
                {
                    Debug.WriteLine("Preheat mode cancelled while auger was running.");
                }
            }
            else
            {
                SetMode(SmokerMode.Hold);
            }
        }

        private async Task StartFire()
        {
            Debug.WriteLine("Starting Igniter thread.");
            while (true)
            {
                if (IsCookingMode(_mode) &&
                    _tempArray.GrillTemp < _ignitionTemp &&
                    !_fireStarted)
                {
                    _igniter.On();
                    _igniterOnTime = DateTime.Now;
                }
                else if (IsCookingMode(_mode) &&
                    _tempArray.GrillTemp >= _ignitionTemp)
                {
                    _fireStarted = true;
                    _igniter.Off();
                }
                else
                {
                    _igniter.Off();
                }

                if (_igniter.IsOn && DateTime.Now - _igniterOnTime > _igniterTimeout)
                {
                    Debug.WriteLine("Igniter timeout. Setting error mode.");
                    _igniter.Off();
                    SetMode(SmokerMode.Error);
                }
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }

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

        private async Task Hold()
        {
            _blower.On();
            
            if (_tempArray.GrillTemp < _ignitionTemp)
            {
                Debug.WriteLine("Hold: Not ignited yet - Diverting to SMOKE mode.");
                await Smoke();
                return;
            }

            if (_pid.SetPoint != SetPoint)
            {
                Debug.WriteLine($"PID setpoint: {_pid.SetPoint}. Actual Setpoint: {SetPoint}. Updating.");
                _pid.SetNewSetpoint(SetPoint);
            }

            double u = NormalizeU(_pid.GetControlVariable(_tempArray.GrillTemp));
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
                    await Task.Delay(TimeSpan.FromMilliseconds(-1), _cts.Token);
                }
                SetMode(SmokerMode.Ready);
            }
            catch (TaskCanceledException ex)
            {
                Debug.WriteLine($"{ex} Shutdown mode cancelled.");
            }
        }

        private async Task Ready()
        {
            _blower.Off();
            _igniter.Off();
            _fireStarted = false;

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        private double NormalizeU(double u)
        {
            if (_tempArray.GrillTemp >= SetPoint)
            {
                return _uMin;
            }

            if (_tempArray.GrillTemp >= SetPoint - 5)
            {
                if(u >= 0.5 * _uMax)
                {
                    return 0.5 * _uMax;
                }
            }

            u = Math.Max(u, _uMin);
            u = Math.Min(u, _uMax);
            return u;
        }

        private bool IsCookingMode(SmokerMode mode)
        {
            if ((_mode == SmokerMode.Smoke ||
                _mode == SmokerMode.Hold ||
                _mode == SmokerMode.Preheat))
            {
                return true;
            }
            else
            {
                return false;
            }
        }

    }
}