using System.Device.Gpio;
using Inferno.Api.Interfaces;

namespace Inferno.Api.Devices
{
    public class Blower : RelayDevice
    {
        public Blower(GpioController gpio, int pin) : base(gpio, pin)
        {
            _relayDescription = "Blower";
        }
    }
}