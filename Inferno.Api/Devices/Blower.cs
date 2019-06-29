using System;
using System.Device.Gpio;
using System.Diagnostics;
using Inferno.Api.Interfaces;

namespace Inferno.Api.Devices
{
    public class Blower : IBlower, IDisposable
    {
        GpioController _gpio;
        int _pin;
        bool _isOn;

        public Blower(GpioController gpio, int pin)
        {
            _gpio = gpio;
            _pin = pin;

            // Open the pin and pull it high so blower is off
            _gpio.OpenPin(_pin, PinMode.Output);
            _gpio.Write(_pin, 1);

            _isOn = false;
        }

        public bool IsOn => _isOn;
        public void On()
        {
            if (!_isOn)
            {
                Debug.WriteLine("Turning Blower ON");
            }
            _gpio.Write(_pin, 0);
            _isOn = true;
        }

        public void Off()
        {
            if (_isOn)
            {
                Debug.WriteLine("Turning Blower OFF");
            }
            
            _gpio.Write(_pin, 1);
            _isOn = false;
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

                // TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
                // TODO: set large fields to null.

                disposedValue = true;
            }
        }

        // TODO: override a finalizer only if Dispose(bool disposing) above has code to free unmanaged resources.
        // ~Blower()
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