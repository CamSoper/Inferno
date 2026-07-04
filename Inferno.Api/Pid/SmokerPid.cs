using System;
using Inferno.Common.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Inferno.Api.Pid
{
    public class SmokerPid
    {
        double _PB;
        double _Ti;
        double _Td;

        double _integral;
        double _iMax = 0.5;

        // Monotonic timestamp of the last update. Using TimeProvider.GetTimestamp
        // (Stopwatch-backed) instead of wall-clock DateTime keeps dt correct even when
        // the Pi's clock steps on an NTP sync mid-cook.
        readonly TimeProvider _timeProvider;
        readonly ILogger<SmokerPid> _logger;
        long _lastTimestamp;
        double _lastTemp;

        public double SetPoint { get; set; }
        public SmokerPid(double PB, double Ti, double Td, TimeProvider? timeProvider = null, ILogger<SmokerPid>? logger = null)
        {
            _PB = PB;
            _Ti = Ti;
            _Td = Td;
            _timeProvider = timeProvider ?? TimeProvider.System;
            _logger = logger ?? NullLogger<SmokerPid>.Instance;
            _lastTimestamp = _timeProvider.GetTimestamp();
            // NaN means "no valid previous sample yet" — the next reading seeds state
            // instead of computing a derivative/integral across an unknown gap.
            _lastTemp = double.NaN;
        }

        public double GetControlVariable(double currentTemp)
        {
            long now = _timeProvider.GetTimestamp();

            if (double.IsNaN(currentTemp))
            {
                // Sensor dropout: hold the integral, advance the clock, and force the
                // next valid reading to re-seed so we don't compute a bogus derivative
                // across the gap.
                _lastTimestamp = now;
                _lastTemp = double.NaN;
                return 0;
            }

            double error = currentTemp - SetPoint;
            double P = GainP() * error;

            if (double.IsNaN(_lastTemp))
            {
                // First reading (or first after a dropout): seed state and return
                // proportional-only. A stale/huge dt here would otherwise spike the
                // integral and derivative.
                _lastTemp = currentTemp;
                _lastTimestamp = now;
                _logger.LogTrace("u={U} (seed)", P);
                return P;
            }

            double dtSeconds = _timeProvider.GetElapsedTime(_lastTimestamp, now).TotalSeconds;

            double I;
            double D = 0;
            if (dtSeconds > 0)
            {
                _integral += error * dtSeconds;
                _integral = _integral.Clamp(-IntegralMax(), IntegralMax());
                I = GainI() * _integral;

                double derivative = (currentTemp - _lastTemp) / dtSeconds;
                D = GainD() * derivative;
            }
            else
            {
                // Two calls in the same instant: no time elapsed, so don't accumulate
                // the integral or divide by zero for the derivative.
                I = GainI() * _integral;
            }

            double u = P + I + D;
            _logger.LogTrace("u={U} ({P}+{I}+{D})", u, P, I, D);

            _lastTemp = currentTemp;
            _lastTimestamp = now;

            return u;
        }


        private double GainP()
        {
            return -1 / _PB;
        }

        private double GainI()
        {
            return GainP() / _Ti; 
        }

        private double GainD()
        {
            return GainP() * _Td;
        }

        /// <summary>
        /// Calculates a max integral value to prevent integral windup.
        /// </summary>
        /// <see cref="https://github.com/DBorello/PiSmoker/issues/2#issuecomment-507793461" />
        /// <see cref="https://en.wikipedia.org/wiki/Integral_windup"/>
        private double IntegralMax()
        {
            return Math.Abs(_iMax / GainI());
        }
    }
}
