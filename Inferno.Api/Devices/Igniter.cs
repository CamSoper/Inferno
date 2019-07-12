using System.Device.Gpio;
using Inferno.Api.Interfaces;

namespace Inferno.Api.Devices
{
    public class Igniter : RelayDevice, IIgniter
    {
        public Igniter(GpioController gpio, int pin) : base(gpio, pin)
        {
            _relayDescription = "Igniter";
        }
    }
}