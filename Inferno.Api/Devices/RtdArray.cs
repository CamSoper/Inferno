using System;
using System.Collections.Concurrent;
using System.Device.Spi.Drivers;
using System.Threading.Tasks;
using Inferno.Api.Interfaces;
using Iot.Device.Mcp3008;
using System.Linq;

namespace Inferno.Api.Devices
{
    public class RtdArray : IRtdArray, IDisposable
    {
        Mcp3008 _adc;
        ConcurrentQueue<double> _grillResistances;
        ConcurrentQueue<double> _probeResistances;

        Task _adcReadTask;

        public RtdArray(UnixSpiDevice spi)
        {
            _adc = new Mcp3008(spi);
            _grillResistances = new ConcurrentQueue<double>();
            _probeResistances = new ConcurrentQueue<double>();

            _adcReadTask = ReadAdc();
        }

        public double GrillTemp => Math.Round(RtdTempFahrenheitFromResistance(_grillResistances.Average()), 0);

        public double ProbeTemp => Math.Round(RtdTempFahrenheitFromResistance(_probeResistances.Average()), 0);

        private async Task ReadAdc()
        {
            while (true)
            {
                int grillValue = _adc.Read(0, Mcp3008.InputConfiguration.SingleEnded);
                int probeValue = _adc.Read(1, Mcp3008.InputConfiguration.SingleEnded);

                _grillResistances.Enqueue(CalculateResistanceFromAdc(grillValue));
                _probeResistances.Enqueue(CalculateResistanceFromAdc(probeValue));
                while (_grillResistances.Count > 100)
                {
                    double temp;
                    _grillResistances.TryDequeue(out temp);
                }
                while (_probeResistances.Count > 100)
                {
                    double temp;
                    _probeResistances.TryDequeue(out temp);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(10));
            }
        }

        static double CalculateResistanceFromAdc(double adcValue)
        {
            double rtdV = (adcValue / 1023) * 3.3;
            return ((3.3 * 1000) - (rtdV * 1000)) / rtdV;
        }

        static double RtdTempFahrenheitFromResistance(double Resistance)
        {
            double A = 3.90830e-3; // Coefficient A
            double B = -5.775e-7; // Coefficient B
            double ReferenceResistor = 1000; 

            double TempCelsius = (-A + Math.Sqrt(A * A - 4 * B * (1 - Resistance / ReferenceResistor))) / (2 * B);
            return TempCelsius * 9 / 5 + 32;
        }


        #region IDisposable Support
        private bool disposedValue = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    _adc.Dispose();
                }

                disposedValue = true;
            }
        }

        // TODO: override a finalizer only if Dispose(bool disposing) above has code to free unmanaged resources.
        // ~TempProbes()
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