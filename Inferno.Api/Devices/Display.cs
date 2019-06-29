using System;
using System.Device.I2c.Drivers;
using Inferno.Api.Interfaces;
using Iot.Device.CharacterLcd;
using Iot.Device.Mcp23xxx;

namespace Inferno.Api.Devices
{
    public class Display : IDisplay, IDisposable
    {
        UnixI2cDevice _i2c;
        Mcp23008 _mcp;
        Lcd2004 _lcd;

        public Display(UnixI2cDevice i2c)
        {
            _i2c = i2c;
            _mcp = new Mcp23008(_i2c);
            _lcd = new Lcd2004(registerSelectPin: 0, enablePin: 2, dataPins: new int[] { 4, 5, 6, 7 }, backlightPin: 3, backlightBrightness: 0.1f, readWritePin: 1, controller: _mcp); 
        }

        public void DisplayInfo(double grillTemp, double probeTemp, string status, bool isIgniterOn = false)
        {
            string grillLabel = "Grill";
            string probeLabel = "Probe";
            string grillValue = $"{grillTemp}*F";
            string probeValue = (Double.IsNaN(probeTemp)) ? "Unplugged" : $"{probeTemp}*F"; 

            _lcd.SetCursorPosition(0, 0);
            _lcd.Write(JustifyWithSpaces(grillLabel, probeLabel));
            _lcd.SetCursorPosition(0, 1);
            _lcd.Write(JustifyWithSpaces(grillValue, probeValue));
            _lcd.SetCursorPosition(0, 2);
            _lcd.Write(new string('-', 20));
            _lcd.SetCursorPosition(0, 3);
            if (isIgniterOn)
            {
                _lcd.Write(JustifyWithSpaces(status, "Ignite"));
            }
            else
            {
                _lcd.Write(status.PadRight(20));
            }
        }

        public void DisplayText(string line1 = "", string line2 = "", string line3 = "", string line4 = "")
        {
            _lcd.SetCursorPosition(0, 0);
            _lcd.Write(line1.PadRight(20));
            _lcd.SetCursorPosition(0, 1);
            _lcd.Write(line2.PadRight(20));
            _lcd.SetCursorPosition(0, 2);
            _lcd.Write(line3.PadRight(20));
            _lcd.SetCursorPosition(0, 3);
            _lcd.Write(line4.PadRight(20));
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

        #region IDisposable Support
        private bool disposedValue = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    DisplayText("Shutting down...", "", "", "Goodbye!".PadLeft(20));
                    _lcd.Dispose();
                    _mcp.Dispose();
                }

                // TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
                // TODO: set large fields to null.

                disposedValue = true;
            }
        }

        // TODO: override a finalizer only if Dispose(bool disposing) above has code to free unmanaged resources.
        // ~Display()
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