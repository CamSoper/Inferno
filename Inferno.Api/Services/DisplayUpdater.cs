using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Inferno.Api.Interfaces;
using Inferno.Api.Models;

namespace Inferno.Api.Services
{
    public class DisplayUpdater
    {
        ISmoker _smoker;
        IDisplay _display;

        bool _heartbeatFlag;

        Task _updateDisplayLoop;

        public DisplayUpdater(ISmoker smoker, IDisplay display)
        {
            _smoker = smoker;
            _display = display;
            _heartbeatFlag = false;
            _updateDisplayLoop = UpdateDisplayLoop();
        }

        private async Task UpdateDisplayLoop()
        {
            Debug.WriteLine("Starting display thread.");
            while (true)
            {
                try
                {
                    switch (_smoker.Mode)
                    {
                        case SmokerMode.Ready:
                            _display.DisplayText(DateTime.Now.ToShortDateString().PadLeft(20),
                                DateTime.Now.ToShortTimeString().PadLeft(20),
                                new string('-', 20),
                                "Ready");
                            break;

                        case SmokerMode.Shutdown:
                            _display.DisplayInfo(_smoker.Temps, "Shutting Down", HardwareStatus());
                            break;

                        case SmokerMode.Smoke:
                            _display.DisplayInfo(_smoker.Temps, $"Smoke P-{_smoker.PValue}", HardwareStatus());
                            break;

                        case SmokerMode.Error:
                            _display.DisplayInfo(_smoker.Temps, $"Shutdown: Fire fault", "");
                            break;

                        case SmokerMode.Hold:
                        case SmokerMode.Preheat:
                        default:
                            _display.DisplayInfo(_smoker.Temps, $"{_smoker.Mode} {_smoker.SetPoint}*F", HardwareStatus());
                            break;
                    }

                    _heartbeatFlag = !_heartbeatFlag;
                    await Task.Delay(TimeSpan.FromSeconds(1));
                }
                catch (Exception ex)
                {
                    string errorText = $"Display updater exception! {ex} {ex.StackTrace}";
                    Console.WriteLine(errorText);
                    Debug.WriteLine(errorText);
                }
            }
        }

        private string HardwareStatus()
        {
            string fire = " ";
            if (_smoker.Status.IgniterOn)
            {
                fire = "I";
            }
            else if (!_smoker.Status.FireHealthy)
            {
                fire = "F";
            }
            string auger = (_smoker.Status.AugerOn) ? "A" : " ";
            string heartbeat = (_heartbeatFlag) ? "*" : " ";
            return $"{fire}{auger}{heartbeat}";
        }
    }
}