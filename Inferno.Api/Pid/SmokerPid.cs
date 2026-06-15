using System;
using System.Diagnostics;
using Inferno.Common.Extensions;

namespace Inferno.Api.Pid
{
    public class SmokerPid
    {
        double _PB;
        double _Ti;
        double _Td;

        double _integral;
        double _iMax = 0.5;

        DateTime _lastUpdate;
        double _lastTemp;
        
        public double SetPoint { get; set; }
        public SmokerPid(double PB, double Ti, double Td)
        {
            _PB = PB;
            _Ti = Ti;
            _Td = Td;
            _lastUpdate = DateTime.Now;
            // NaN means "no valid previous sample yet" — the next reading seeds state
            // instead of computing a derivative/integral across an unknown gap.
            _lastTemp = double.NaN;
        }

        public double GetControlVariable(double currentTemp)
        {
            DateTime now = DateTime.Now;

            if (double.IsNaN(currentTemp))
            {
                // Sensor dropout: hold the integral, advance the clock, and force the
                // next valid reading to re-seed so we don't compute a bogus derivative
                // across the gap.
                _lastUpdate = now;
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
                _lastUpdate = now;
                Debug.WriteLine($"u={P} (seed)");
                return P;
            }

            double dtSeconds = (now - _lastUpdate).TotalSeconds;

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
            Debug.WriteLine($"u={u} ({P}+{I}+{D})");

            _lastTemp = currentTemp;
            _lastUpdate = now;

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
