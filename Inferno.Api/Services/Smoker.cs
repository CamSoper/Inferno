using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
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
        int _ignitionTemp = 145;
        int _shutdownBlowerTime = 10;
        int _igniterTimeout = 10;
        DateTime _igniterOnTime;

        CancellationTokenSource _cts;
        
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

            _mode = SmokerMode.Standby;
            SetPoint = _minSetPoint;
            PValue = 2;

            CancellationTokenSource _cts = new CancellationTokenSource();

            _displayTask = UpdateDisplay();
            _modeTask = DoMode();
            _igniterTask = WatchIgniter();
        }

        public SmokerMode Mode => _mode;
        public int SetPoint { get; set; }
        public int PValue { get; set; }
        public Temps Temps => new Temps(){ GrillTemp = _tempArray.GrillTemp,
                                            ProbeTemp = Double.IsNaN(_tempArray.ProbeTemp) ? -1 : _tempArray.ProbeTemp };
        
        public bool SetMode(SmokerMode mode)
        {
            Debug.WriteLine($"Setting mode {mode}.");
            
            SmokerMode currentMode = _mode;

            // Must run Shutdown or Error before Standby
            if (mode == SmokerMode.Standby && 
                currentMode != SmokerMode.Shutdown &&
                currentMode != SmokerMode.Error)
            {
                return false;   
            }

            // Can't go to Shutdown from Standby
            if (currentMode == SmokerMode.Standby &&
                 mode == SmokerMode.Shutdown)
            {
                return false;
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
            while(true)
            {
                switch (_mode)
                {
                    case SmokerMode.Standby:
                        _display.DisplayText(DateTime.Now.ToShortDateString().PadLeft(20), 
                            DateTime.Now.ToShortTimeString().PadLeft(20), 
                            new string('-', 20), 
                            "Standby");            
                        break;

                    case SmokerMode.Shutdown:
                        _display.DisplayInfo(_tempArray.GrillTemp, _tempArray.ProbeTemp, "Shutting Down", _igniter.IsOn);
                        break;

                    case SmokerMode.Hold:
                        _display.DisplayInfo(_tempArray.GrillTemp, _tempArray.ProbeTemp, $"Hold {SetPoint}*F", _igniter.IsOn);
                        break;

                    case SmokerMode.Smoke:
                        _display.DisplayInfo(_tempArray.GrillTemp, _tempArray.ProbeTemp, $"Smoke P-{PValue}", _igniter.IsOn);
                        break;

                    case SmokerMode.Error:
                        _display.DisplayInfo(_tempArray.GrillTemp, _tempArray.ProbeTemp, $"Error-Clear fire pot");
                        break;
                }

                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }

        private async Task DoMode()
        {
            Debug.WriteLine("Starting mode thread.");
            while(true)
            {
                using(_cts = new CancellationTokenSource())
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

                        case SmokerMode.Standby:
                            await Standby();          
                            break;
                    }
                }
            }
        }

        private async Task WatchIgniter()
        {
            Debug.WriteLine("Starting Igniter watcher thread.");
            while (true)
            {
                if((_mode == SmokerMode.Smoke ||
                    _mode == SmokerMode.Hold) &&
                    _tempArray.GrillTemp < _ignitionTemp)
                {
                    _igniter.On();
                    _igniterOnTime = DateTime.Now;
                }
                else
                {
                    _igniter.Off();
                }

                if (_igniter.IsOn && DateTime.Now - _igniterOnTime > TimeSpan.FromMinutes(_igniterTimeout))
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
            if(!_cts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(45 + (10 * PValue)), _cts.Token);
                }
                catch(TaskCanceledException ex)
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
            throw new NotImplementedException();
        }

        private async Task Shutdown()
        {
            _blower.On();
            _igniter.Off();
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(_shutdownBlowerTime), _cts.Token);
                if(_mode == SmokerMode.Error)
                {
                    _blower.Off();
                    await Task.Delay(TimeSpan.FromMilliseconds(-1), _cts.Token);
                }
                SetMode(SmokerMode.Standby);
            }
            catch(TaskCanceledException ex)
            {
                Debug.WriteLine($"{ex} Shutdown mode cancelled.");
            }
        }

        private async Task Standby()
        {
            _blower.Off();
            _igniter.Off();

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

    }
}