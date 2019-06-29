using System;
using System.Diagnostics;
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
        int _ignitionTemp = 140;
        int _shutdownBlowerTime = 10;

        Task _modeTask;
        Task _displayTask;

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

            _displayTask = UpdateDisplay();
            _modeTask = DoMode();
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
                        _display.DisplayInfo(_tempArray.GrillTemp, _tempArray.ProbeTemp, $"Shutting Down/Error");
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

        private async Task Smoke()
        {
            _blower.On();
            if(_tempArray.GrillTemp < _ignitionTemp)
            {
                _igniter.On();
            }
            else
            {
                _igniter.Off();
            }
            
            await _auger. Run(TimeSpan.FromSeconds(15));
            if(_mode == SmokerMode.Smoke)
            {
                await Task.Delay(TimeSpan.FromSeconds(45 + (10 * PValue)));
            }         
        }

        private async Task Hold()
        {
            _blower.On();
            await Task.Delay(TimeSpan.FromSeconds(5));
        }

        private async Task Shutdown()
        {
            _blower.On();
            _igniter.Off();
            await Task.Delay(TimeSpan.FromMinutes(_shutdownBlowerTime));
            SetMode(SmokerMode.Standby);
        }

        private async Task Standby()
        {
            _blower.Off();
            _igniter.Off();

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

    }
}