using System;
using System.Device.Gpio;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Inferno.Api.Interfaces;

namespace Inferno.Api.Devices
{
    public class Auger : IAuger, IDisposable
    {
        GpioController _gpio;
        int _pin;
        public Auger(GpioController gpio, int pin)
        {
            _gpio = gpio;
            _pin = pin;

            // Open the pin and pull it high so auger is off
            _gpio.OpenPin(_pin, PinMode.Output);
            _gpio.Write(_pin, 1);
        }

        public async Task Run(TimeSpan RunTime, CancellationToken token)
        {
            Debug.WriteLine($"Auger running: {RunTime.Seconds} seconds.");

            // Run the auger
            _gpio.Write(_pin, 0);
            Debug.WriteLine("Auger ON.");

            try
            {
                await Task.Delay(RunTime, token);
            }
            catch(TaskCanceledException ex)
            {
                Debug.WriteLine($"{ex} Running auger cancelled.");
            }

            _gpio.Write(_pin, 1);
            Debug.WriteLine("Auger OFF.");
        }

        #region IDisposable Support
        private bool disposedValue = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    _gpio.ClosePin(_pin);
                }


                disposedValue = true;
            }
        }

        // TODO: override a finalizer only if Dispose(bool disposing) above has code to free unmanaged resources.
        // ~Auger()
        // {
        //   // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
        //   Dispose(false);
        // }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);
            // TODO: uncomment the following line if the finalizer is overridden above.
            // GC.SuppressFinalize(this);
        }
        #endregion
    }
}