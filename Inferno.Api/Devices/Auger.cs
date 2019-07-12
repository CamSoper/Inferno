using System;
using System.Device.Gpio;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Inferno.Api.Interfaces;

namespace Inferno.Api.Devices
{
    public class Auger : RelayDevice, IAuger
    {
        public Auger(GpioController gpio, int pin) : base(gpio, pin)
        {
            _relayDescription = "Auger";
        }

        public async Task Run(TimeSpan RunTime, CancellationToken token)
        {
            Debug.WriteLine($"Auger running: {RunTime.Seconds} seconds.");

            // Run the auger
            this.On();

            try
            {
                await Task.Delay(RunTime, token);
            }
            catch(TaskCanceledException ex)
            {
                Debug.WriteLine($"{ex} Running auger cancelled.");
            }

            this.Off();
        }
    }
}