using System;
using System.Collections.Concurrent;
using System.Device.Spi;
using System.Diagnostics;
using System.Threading.Tasks;
using Inferno.Api.Interfaces;
using System.Linq;
using Iot.Device.Adc;

namespace Inferno.Api.Devices
{
    public class RtdArray : IRtdArray, IDisposable
    {
        Mcp3008 _adc;
        ConcurrentQueue<double> _grillResistances;
        ConcurrentQueue<double> _probeResistances;

        // Physically reasonable temperature range for a pellet grill.
        // Below -20F the sensor or wiring is likely failed/shorted.
        // Above 1000F something is very wrong (firepot max is ~600F).
        const double MinValidTempF = -20;
        const double MaxValidTempF = 1000;

        Task _adcReadTask;

        public RtdArray(SpiDevice spi)
        {
            _adc = new Mcp3008(spi);
            _grillResistances = new ConcurrentQueue<double>();
            _probeResistances = new ConcurrentQueue<double>();

            _adcReadTask = ReadAdc();
        }

        public double GrillTemp => GetTemp(_grillResistances);

        public double ProbeTemp => GetTemp(_probeResistances);

        private static double GetTemp(ConcurrentQueue<double> resistances)
        {
            if (resistances.IsEmpty) return Double.NaN;
            return Math.Round(RtdTempFahrenheitFromResistance(resistances.Average()), 0);
        }

        private async Task ReadAdc()
        {
            while (true)
            {
                int grillValue;
                int probeValue;

                try
                {
                    grillValue = _adc.Read(0);
                    probeValue = _adc.Read(1);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"{DateTime.Now} {ex.Message} {ex.StackTrace}");
                    await Task.Delay(TimeSpan.FromMilliseconds(10));
                    continue;
                }

                EnqueueIfValid(_grillResistances, grillValue, "Grill");
                EnqueueIfValid(_probeResistances, probeValue, "Probe");

                await Task.Delay(TimeSpan.FromMilliseconds(10));
            }
        }

        private void EnqueueIfValid(ConcurrentQueue<double> queue, int adcValue, string sensorName)
        {
            double resistance = CalculateResistanceFromAdc(adcValue);
            double tempF = RtdTempFahrenheitFromResistance(resistance);

            if (Double.IsNaN(tempF) || Double.IsInfinity(tempF) ||
                tempF < MinValidTempF || tempF > MaxValidTempF)
            {
                Debug.WriteLine($"{sensorName} sensor: rejected reading {tempF:F1}F (ADC={adcValue}, R={resistance:F1})");
                return;
            }

            queue.Enqueue(resistance);
            while (queue.Count > 100)
            {
                queue.TryDequeue(out _);
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