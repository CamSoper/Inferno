using System;
using System.Device.Gpio;
using System.Diagnostics;
using Inferno.Api.Interfaces;

namespace Inferno.Api.Devices
{
    public abstract class RelayDevice : IRelayDevice, IDisposable
    {
        GpioController _gpio;
        int _pin;
        internal string _relayDescription = "Relay";

        public bool IsOn => _gpio.IsPinOpen(_pin) && _gpio.Read(_pin) == PinValue.Low;

        public RelayDevice(GpioController gpio, int pin)
        {
            _gpio = gpio;
            _pin = pin;

            _gpio.OpenPin(_pin, PinMode.Output);
            // ensure the relay is off
            _gpio.Write(_pin, PinValue.High);
        }

        public void On()
        {
            if (!IsOn)
            {
                Debug.WriteLine($"{_relayDescription} ON.");
                _gpio.Write(_pin, PinValue.Low);
            }
        }

        public void Off()
        {
            if (IsOn)
            {
                Debug.WriteLine($"{_relayDescription} OFF.");
                _gpio.Write(_pin, PinValue.High);
            }
        }


        #region IDisposable Support
        private bool disposedValue = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    if(_gpio.IsPinOpen(_pin))
                    {
                        _gpio.Write(_pin, PinValue.High);
                        _gpio.ClosePin(_pin);
                    }
                }

                // TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
                // TODO: set large fields to null.

                disposedValue = true;
            }
        }

        // TODO: override a finalizer only if Dispose(bool disposing) above has code to free unmanaged resources.
        // ~RelayDevice()
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