using System.Device.Gpio;
using System.Device.Gpio.Drivers;
using System.Device.Spi;
using Inferno.Api.Services;
using Inferno.Api.Devices;
using Inferno.Api.Interfaces;
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

var smoker = new Smoker(new Auger(_gpio, 22),
                        new Blower(_gpio, 21),
                        new Igniter(_gpio, 23),
                        new RtdArray(_spi),
                        new Display());
builder.Services.AddSingleton<ISmoker>(smoker);

// Cook history: a SQLite store plus a background logger that samples the smoker and
// records each cook as a session. The logger only depends on ISmoker.Status, so it
// stays decoupled from the safety-critical state machine. DB lives outside the
// publish dir (which the deploy replaces) at ~/inferno/data/inferno.db; override
// with INFERNO_DB_PATH.
string dbPath = Environment.GetEnvironmentVariable("INFERNO_DB_PATH")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "inferno", "data", "inferno.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
var cookLogStore = new SqliteCookLogStore(dbPath);
cookLogStore.Initialize();
builder.Services.AddSingleton<ICookLogStore>(cookLogStore);

var cookLogger = new CookLogger(smoker, cookLogStore);
builder.Services.AddSingleton(cookLogger);

var app = builder.Build();

app.MapControllers();

// On a clean stop (systemctl stop / SIGTERM / Ctrl-C), tear the smoker down so the
// auger and igniter relays are de-energized instead of being left in their last
// commanded state. Smoker.Dispose() drives a hard safe-off and releases the devices;
// the shared GPIO controller is disposed afterward.
app.Lifetime.ApplicationStopping.Register(() =>
{
    // Order matters: flush + close the active cook session before the smoker tears
    // down, then close the DB, then release the GPIO.
    cookLogger.Dispose();
    (app.Services.GetService<ISmoker>() as IDisposable)?.Dispose();
    cookLogStore.Dispose();
    _gpio.Dispose();
});

app.Run();
