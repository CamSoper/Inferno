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
        IRtdArray _rtdArray;
        IDisplay _display;

        int _maxSetPoint = 450;
        int _minSetPoint = 180;
        int _ignitionTemp = 140;

        TimeSpan _shutdownBlowerTimeout = TimeSpan.FromMinutes(10);
        TimeSpan _igniterTimeout = TimeSpan.FromMinutes(10);
        TimeSpan _fireTimeout = TimeSpan.FromMinutes(5);
        DateTime _igniterOnTime;
        TimeSpan _holdCycle = TimeSpan.FromSeconds(20);

        CancellationTokenSource _cts;
        PidController _pid;
        DateTime _lastPidUpdate;
        DateTime _lastModeChange;
        bool _fireCheck;
        DateTime _fireCheckTime;
        bool _heartbeatFlag;

        double _uMax = 1.0;
        double _uMin = 0.15;

        bool _fireStarted;
        Task _modeLoopTask;
        Task _displayTask;
        Task _fireMinderTask;

        public Smoker(IAuger auger,
                        IBlower blower,
                        IIgniter igniter,
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

            _pid = new PidController(60.0, 180.0, 45.0);

            _displayTask = UpdateDisplay();
            _modeLoopTask = ModeLoop();
            _fireMinderTask = FireMinder();
        }

        public SmokerMode Mode => _mode;
        public int SetPoint { get; set; }
        public int PValue { get; set; }
        public Temps Temps => new Temps()
        {
            GrillTemp = _rtdArray.GrillTemp,
            ProbeTemp = Double.IsNaN(_rtdArray.ProbeTemp) ? -1 : _rtdArray.ProbeTemp
        };

        public SmokerStatus Status => new SmokerStatus()
        {
            AugerOn = _auger.IsOn,
            BlowerOn = _blower.IsOn,
            IgniterOn = _igniter.IsOn,
            Temps = this.Temps,
            FireHealthy = !_fireCheck,
            Mode = this.Mode,
            ModeString = this.Mode.ToString(),
            SetPoint = this.SetPoint,
            ModeTime = _lastModeChange,
            CurrentTime = DateTime.Now
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
            _lastModeChange = DateTime.Now;
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
                try
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
                            _display.DisplayInfo(_rtdArray.GrillTemp, _rtdArray.ProbeTemp, "Shutting Down", HardwareStatus());
                            break;

                        case SmokerMode.Hold:
                            _display.DisplayInfo(_rtdArray.GrillTemp, _rtdArray.ProbeTemp, $"Hold {SetPoint}*F", HardwareStatus());
                            break;

                        case SmokerMode.Preheat:
                            _display.DisplayInfo(_rtdArray.GrillTemp, _rtdArray.ProbeTemp, $"Preheat {SetPoint}*F", HardwareStatus());
                            break;

                        case SmokerMode.Smoke:
                            _display.DisplayInfo(_rtdArray.GrillTemp, _rtdArray.ProbeTemp, $"Smoke P-{PValue}", HardwareStatus());
                            break;

                        case SmokerMode.Error:
                            _display.DisplayInfo(_rtdArray.GrillTemp, _rtdArray.ProbeTemp, $"Error:Clear fire pot", "");
                            break;
                    }

                    _heartbeatFlag = !_heartbeatFlag;
                    await Task.Delay(TimeSpan.FromSeconds(1));
                }
                catch (Exception ex)
                {
                    string errorText = $"Display exception! {ex} {ex.StackTrace}";
                    Console.WriteLine(errorText);
                    Debug.WriteLine(errorText);
                }
            }
        }

        private string HardwareStatus()
        {
            string igniter = (_igniter.IsOn) ? "I" : " ";
            string auger = (_auger.IsOn) ? "A" : " ";
            string heartbeat = (_heartbeatFlag) ? "*" : " ";
            return $"{igniter}{auger}{heartbeat}";
        }

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
                catch (Exception ex)
                {
                    string errorText = $"Mode loop exception! {ex} {ex.StackTrace}";
                    Console.WriteLine(errorText);
                    Debug.WriteLine(errorText);
                }

            }
        }

        private async Task Preheat()
        {
            _blower.On();

            if (!_fireStarted)
            {
                Debug.WriteLine("Preheat: Not ignited yet - Diverting to SMOKE mode.");
                await Smoke();
                return;
            }

            if (_rtdArray.GrillTemp < SetPoint - 10)
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

        private async Task FireMinder()
        {
            Debug.WriteLine("Starting Fire Minder thread.");
            while (true)
            {
                try
                {
                    if (IsCookingMode(_mode) &&
                        _rtdArray.GrillTemp < _ignitionTemp &&
                        !_fireStarted)
                    {
                        _igniter.On();
                        _igniterOnTime = DateTime.Now;
                    }
                    else if (IsCookingMode(_mode) &&
                        _rtdArray.GrillTemp >= _ignitionTemp)
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
                        string errorText = "Igniter timeout. Setting error mode.";
                        Debug.WriteLine(errorText);
                        Console.WriteLine(errorText);
                        _igniter.Off();
                        SetMode(SmokerMode.Error);
                    }

                    if (_fireStarted && _rtdArray.GrillTemp < _ignitionTemp && !_fireCheck)
                    {
                        _fireCheck = true;
                        _fireCheckTime = DateTime.Now;
                    }
                    else if(_fireCheck && _rtdArray.GrillTemp >= _ignitionTemp)
                    {
                        _fireCheck = false;
                    }
                    else if(_fireCheck && DateTime.Now - _fireCheckTime > _fireTimeout)
                    {
                        string errorText = "Fire timeout. Setting error mode.";
                        Debug.WriteLine(errorText);
                        Console.WriteLine(errorText);
                        SetMode(SmokerMode.Error);
                    }

                    await Task.Delay(TimeSpan.FromSeconds(1));
                }
                catch (Exception ex)
                {
                    string errorText = $"Fire Minder loop exception! {ex} {ex.StackTrace}";
                    Console.WriteLine(errorText);
                    Debug.WriteLine(errorText);
                }
            }
        }

        private async Task Smoke()
        {
            _blower.On();
            SetPoint = _minSetPoint;

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

            if (!_fireStarted)
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

            double u = NormalizeU(_pid.GetControlVariable(_rtdArray.GrillTemp));
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
            _fireCheck = false;
            SetPoint = _minSetPoint;

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        private double NormalizeU(double u)
        {
            if (_rtdArray.GrillTemp >= SetPoint)
            {
                return _uMin;
            }

            if (_rtdArray.GrillTemp >= SetPoint - 5)
            {
                if (u >= 0.5 * _uMax)
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