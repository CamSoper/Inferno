using System;
using System.Device.Gpio;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Inferno.Api.Interfaces;

namespace Inferno.Api.Devices
{
    public class Auger : RelayDevice
    {
        public Auger(GpioController gpio, int pin) : base(gpio, pin)
        {
            _relayDescription = "Auger";
        }
    }
}