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
        }

        public double GetControlVariable(double currentTemp)
        {
            if (double.IsNaN(currentTemp))
                return 0;

            double error = currentTemp - SetPoint;
            
            double P = GainP() * error;

            TimeSpan dT = DateTime.Now - _lastUpdate;
            _integral += error * dT.TotalSeconds;
            _integral = _integral.Clamp(-IntegralMax(), IntegralMax());
            double I = GainI() * _integral;

            double derivative = (currentTemp - _lastTemp) / dT.Seconds;
            double D = GainD() * derivative;

            double u = P + I + D;
            Debug.WriteLine($"u={u} ({P}+{I}+{D})");

            _lastTemp = currentTemp;
            _lastUpdate = DateTime.Now;

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
