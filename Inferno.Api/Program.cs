using System.Device.Gpio;
using System.Device.Gpio.Drivers;
using System.Device.Spi;
using Inferno.Api.Services;
using Inferno.Api.Devices;
using Inferno.Common.Interfaces;

GpioController _gpio = new GpioController(PinNumberingScheme.Logical, new RaspberryPi3Driver());

SpiConnectionSettings _spiConnSettings = new SpiConnectionSettings(0, 0)
{
    ClockFrequency = 1000000,
    Mode = SpiMode.Mode0
};
SpiDevice _spi = SpiDevice.Create(_spiConnSettings);

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();

builder.Services.AddSingleton<ISmoker>(new Smoker(new Auger(_gpio, 22),
                                                new Blower(_gpio, 21),
                                                new Igniter(_gpio, 23),
                                                new RtdArray(_spi),
                                                new Display()));

var app = builder.Build();

app.MapControllers();

// On a clean stop (systemctl stop / SIGTERM / Ctrl-C), tear the smoker down so the
// auger and igniter relays are de-energized instead of being left in their last
// commanded state. Smoker.Dispose() drives a hard safe-off and releases the devices;
// the shared GPIO controller is disposed afterward.
app.Lifetime.ApplicationStopping.Register(() =>
{
    (app.Services.GetService<ISmoker>() as IDisposable)?.Dispose();
    _gpio.Dispose();
});

app.Run();
