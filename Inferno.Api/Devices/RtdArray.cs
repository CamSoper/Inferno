using System;
using System.Collections.Concurrent;
using System.Device.Spi;
using System.Diagnostics;
using System.Threading.Tasks;
using Inferno.Api.Interfaces;
using System.Linq;
using Iot.Device.Adc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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

        // A brief burst of out-of-range readings is noise and is filtered out. A
        // sustained run means the sensor is disconnected/failed, so we drain the
        // stale samples and let the temperature read NaN (surfaced as "Unplugged")
        // instead of holding the last good value forever. ~2s at the 10ms read rate.
        internal const int MaxConsecutiveInvalidReadings = 200;
        int _grillInvalidCount;
        int _probeInvalidCount;

        Task _adcReadTask;
        readonly CancellationTokenSource _stopCts = new();
        readonly ILogger<RtdArray> _logger;

        public RtdArray(SpiDevice spi, ILogger<RtdArray>? logger = null)
        {
            _adc = new Mcp3008(spi);
            _logger = logger ?? NullLogger<RtdArray>.Instance;
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
            while (!_stopCts.IsCancellationRequested)
            {
                int grillValue;
                int probeValue;

                try
                {
                    grillValue = _adc.Read(0);
                    probeValue = _adc.Read(1);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ADC read failed.");
                    try { await Task.Delay(TimeSpan.FromMilliseconds(10), _stopCts.Token); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                EnqueueIfValid(_grillResistances, grillValue, "Grill", ref _grillInvalidCount);
                EnqueueIfValid(_probeResistances, probeValue, "Probe", ref _probeInvalidCount);

                try { await Task.Delay(TimeSpan.FromMilliseconds(10), _stopCts.Token); }
                catch (OperationCanceledException) { break; }
            }
        }

        internal static void EnqueueIfValid(ConcurrentQueue<double> queue, int adcValue, string sensorName, ref int consecutiveInvalid)
        {
            double resistance = CalculateResistanceFromAdc(adcValue);
            double tempF = RtdTempFahrenheitFromResistance(resistance);

            if (Double.IsNaN(tempF) || Double.IsInfinity(tempF) ||
                tempF < MinValidTempF || tempF > MaxValidTempF)
            {
                Debug.WriteLine($"{sensorName} sensor: rejected reading {tempF:F1}F (ADC={adcValue}, R={resistance:F1})");

                // A few bad readings in a row are noise. A sustained run means the
                // sensor is gone — drain the stale samples so the temp reads NaN.
                if (++consecutiveInvalid >= MaxConsecutiveInvalidReadings)
                {
                    queue.Clear();
                    consecutiveInvalid = MaxConsecutiveInvalidReadings; // keep bounded
                }
                return;
            }

            consecutiveInvalid = 0;
            queue.Enqueue(resistance);
            while (queue.Count > 100)
            {
                queue.TryDequeue(out _);
            }
        }

        internal static double CalculateResistanceFromAdc(double adcValue)
        {
            double rtdV = (adcValue / 1023) * 3.3;
            return ((3.3 * 1000) - (rtdV * 1000)) / rtdV;
        }

        internal static double RtdTempFahrenheitFromResistance(double Resistance)
        {
            double A = 3.90830e-3; // Coefficient A
            double B = -5.775e-7; // Coefficient B
            double ReferenceResistor = 1000; 

            double TempCelsius = (-A + Math.Sqrt(A * A - 4 * B * (1 - Resistance / ReferenceResistor))) / (2 * B);
            return TempCelsius * 9 / 5 + 32;
        }


        private bool disposedValue;

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    _stopCts.Cancel();
                    _stopCts.Dispose();
                    _adc.Dispose();
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