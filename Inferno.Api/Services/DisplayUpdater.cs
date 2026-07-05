using Inferno.Api.Interfaces;
using Inferno.Common.Interfaces;
using Inferno.Common.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Inferno.Api.Services
{
    public class DisplayUpdater : IDisposable
    {
        ISmoker _smoker;
        IDisplay _display;
        readonly ILogger<DisplayUpdater> _logger;

        bool _heartbeatFlag;

        Task _updateDisplayLoop;
        readonly CancellationTokenSource _stopCts = new();

        public DisplayUpdater(ISmoker smoker, IDisplay display, ILogger<DisplayUpdater>? logger = null)
        {
            _smoker = smoker;
            _display = display;
            _logger = logger ?? NullLogger<DisplayUpdater>.Instance;
            _heartbeatFlag = false;
            _updateDisplayLoop = UpdateDisplayLoop();
        }

        private async Task UpdateDisplayLoop()
        {
            _logger.LogDebug("Starting display loop.");
            while (!_stopCts.IsCancellationRequested)
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
                            _display.DisplayInfo(_smoker.Temps, $"{_smoker.Mode}", HardwareStatus());
                            break;

                        case SmokerMode.Error:
                            _display.DisplayInfo(_smoker.Temps, $"Shutdown: Fire fault", "");
                            break;

                        case SmokerMode.Sear:
                            _display.DisplayInfo(_smoker.Temps, $"{_smoker.Mode}", HardwareStatus());
                            break;
                            
                        case SmokerMode.Hold:
                        default:
                            _display.DisplayInfo(_smoker.Temps, $"{_smoker.Mode} {_smoker.SetPoint}*F", HardwareStatus());
                            break;
                    }

                    _heartbeatFlag = !_heartbeatFlag;
                    await Task.Delay(TimeSpan.FromSeconds(1), _stopCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Display updater exception. Reinitializing display.");
                    _display.Init();
                }
            }
        }

        public void Dispose()
        {
            _stopCts.Cancel();
            _stopCts.Dispose();
        }

        private string HardwareStatus()
        {
            var status = _smoker.Status;
            string fire = " ";
            if (status.IgniterOn)
            {
                fire = "I";
            }
            else if (!status.FireHealthy)
            {
                fire = "F";
            }
            string auger = (status.AugerOn) ? "A" : " ";
            string heartbeat = (_heartbeatFlag) ? "*" : " ";
            return $"{fire}{auger}{heartbeat}";
        }
    }
}