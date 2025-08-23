using System.Device.Gpio;
using System.Device.Gpio.Drivers;
using System.Device.Spi;
using Inferno.Api.Devices;
using Inferno.Api.Services;
using Inferno.Api.Settings;
using Inferno.Common.Interfaces;

GpioController _gpio = new GpioController(PinNumberingScheme.Logical, new RaspberryPi3Driver());

SpiConnectionSettings _spiConnSettings = new SpiConnectionSettings(0, 0)
{
    ClockFrequency = 1000000,
    Mode = SpiMode.Mode0
};
SpiDevice _spi = SpiDevice.Create(_spiConnSettings);

var builder = WebApplication.CreateBuilder(args);

var smokerSettings = builder.Configuration.GetSection("SmokerSettings").Get<SmokerSettings>() ?? new SmokerSettings();
builder.Services.AddSingleton(smokerSettings);

// Add services to the container.

builder.Services.AddControllers();

builder.Services.AddSingleton<ISmoker>(sp =>
    new Smoker(new Auger(_gpio, smokerSettings.AugerPin),
                new Blower(_gpio, smokerSettings.BlowerPin),
                new Igniter(_gpio, smokerSettings.IgniterPin),
                new RtdArray(_spi),
                new Display(),
                smokerSettings));

var app = builder.Build();

app.MapControllers();

app.Run();
