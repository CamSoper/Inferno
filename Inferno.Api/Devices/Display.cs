using System;
using System.Device.Gpio;
using System.Device.I2c;
using Inferno.Api.Interfaces;
using Inferno.Common.Models;
using Iot.Device.CharacterLcd;
using Iot.Device.Mcp23xxx;
using Iot.Device.Pcx857x;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Inferno.Api.Devices
{
    public class Display : IDisplay, IDisposable
    {
        static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

        I2cDevice? _i2c;
        Pcf8574? _driver;
        Lcd2004? _lcd;
        readonly ILogger<Display> _logger;
        DateTime _lastInitAttempt = DateTime.MinValue;
        bool _loggedUnavailable;

        // The LCD is a nice-to-have. A missing or dead display must never take the
        // controller down with it, so construction and writes fail soft: while the
        // LCD is unavailable, writes are dropped and Init is retried every 30s.
        public Display(ILogger<Display>? logger = null)
        {
            _logger = logger ?? NullLogger<Display>.Instance;
            Init();
        }

        public bool Available => _lcd is not null;

        public void Init()
        {
            _lastInitAttempt = DateTime.UtcNow;
            Teardown();

            try
            {
                _i2c = I2cDevice.Create(new I2cConnectionSettings(1, 0x27));
                _driver = new Pcf8574(_i2c);
                _lcd = new Lcd2004(registerSelectPin: 0,
                        enablePin: 2,
                        dataPins: new int[] { 4, 5, 6, 7 },
                        backlightPin: 3,
                        backlightBrightness: 0.1f,
                        readWritePin: 1,
                        controller: new GpioController(PinNumberingScheme.Logical, _driver));
                if (_loggedUnavailable)
                {
                    _logger.LogWarning("LCD is back.");
                    _loggedUnavailable = false;
                }
            }
            catch (Exception ex)
            {
                Teardown();
                if (!_loggedUnavailable)
                {
                    _logger.LogError(ex, "LCD unavailable; running headless and retrying every {Seconds}s.", RetryInterval.TotalSeconds);
                    _loggedUnavailable = true;
                }
            }
        }

        void Teardown()
        {
            try { _lcd?.Dispose(); } catch { }
            try { _driver?.Dispose(); } catch { }
            try { _i2c?.Dispose(); } catch { }
            _lcd = null;
            _driver = null;
            _i2c = null;
        }

        /// <summary>Returns the LCD if usable, retrying Init on a timer if not.</summary>
        Lcd2004? Lcd()
        {
            if (_lcd is null && DateTime.UtcNow - _lastInitAttempt >= RetryInterval)
            {
                Init();
            }
            return _lcd;
        }

        public void DisplayInfo(Temps temps, string mode, string hardwareStatus)
        {
            var lcd = Lcd();
            if (lcd is null) return;

            string grillLabel = "Grill";
            string probeLabel = "Probe";
            string grillValue = $"{temps.GrillTemp}*F";
            string probeValue = (temps.ProbeTemp == -1) ? "Unplugged" : $"{temps.ProbeTemp}*F"; 

            lcd.SetCursorPosition(0, 0);
            lcd.Write(JustifyWithSpaces(grillLabel, probeLabel));
            lcd.SetCursorPosition(0, 1);
            lcd.Write(JustifyWithSpaces(grillValue, probeValue));
            lcd.SetCursorPosition(0, 2);
            lcd.Write(new string('-', 20));
            lcd.SetCursorPosition(0, 3);
            lcd.Write(JustifyWithSpaces(mode, hardwareStatus));
        }

        public void DisplayText(string line1 = "", string line2 = "", string line3 = "", string line4 = "")
        {
            var lcd = Lcd();
            if (lcd is null) return;

            lcd.SetCursorPosition(0, 0);
            lcd.Write(line1.PadRight(20));
            lcd.SetCursorPosition(0, 1);
            lcd.Write(line2.PadRight(20));
            lcd.SetCursorPosition(0, 2);
            lcd.Write(line3.PadRight(20));
            lcd.SetCursorPosition(0, 3);
            lcd.Write(line4.PadRight(20));
        }

        private string JustifyWithSpaces(string string1, string string2, int maxChars = 20)
        {
            if(string1.Length + string2.Length > maxChars)
            {
                if (string1.Length > 10)
                    string1 = string1.Substring(0,10);

                if (string2.Length > 10)
                    string2 = string2.Substring(0,10);
            }

            string spaces = new string(' ', (maxChars - (string1.Length + string2.Length)));
            return $"{string1}{spaces}{string2}"; 
        }

        private bool disposedValue;

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    try { if (_lcd is not null) DisplayText("Shutting down...", "", "", "Goodbye!".PadLeft(20)); }
                    catch { }
                    // Teardown disposes in dependency order: the LCD writes through the
                    // driver to the I2C bus on Dispose, so the bus must outlive both.
                    Teardown();
                }
                disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}