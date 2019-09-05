using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Device.Gpio;
using System.Device.Gpio.Drivers;
using System.Device.Spi;
using System.Device.I2c;
using Inferno.Api.Services;
using Inferno.Api.Interfaces;
using Inferno.Api.Devices;

namespace Inferno.Api
{
    public class Startup
    {
        GpioController _gpio;
        SpiDevice _spi;
        I2cDevice _i2c;


        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;

            InitHardware();
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            InitHardware();
            services.AddSingleton<ISmoker>(new Smoker(new Auger(_gpio, 22),
                                                new Blower(_gpio, 21),
                                                new Igniter(_gpio, 23),
                                                new RtdArray(_spi),
                                                new Display(_i2c)));
            services.AddControllers();
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }

            app.UseHttpsRedirection();

            app.UseRouting();

            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
            });
        }

        private void InitHardware()
        {
            _gpio = new GpioController(PinNumberingScheme.Logical, new RaspberryPi3Driver());

            SpiConnectionSettings spiConnSettings = new SpiConnectionSettings(0, 0)
            {
                ClockFrequency = 1000000,
                Mode = SpiMode.Mode0
            };
            _spi = SpiDevice.Create(spiConnSettings);

            _i2c = I2cDevice.Create(new I2cConnectionSettings(1, 0x27));
        }
    }
}
