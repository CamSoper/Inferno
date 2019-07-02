using System;
using System.Diagnostics;
using Inferno.Api.Extensions;

namespace Inferno.Api.Pid
{
    public class SmokerPid
    {
        double _PB;
        double _Ti;
        double _Td;

        double _integral;

        DateTime _lastUpdate;
        double _lastActualTemp;

        public double SetPoint { get; set; }
        public SmokerPid(double PB, double Ti, double Td)
        {
            _PB = PB;
            _Ti = Ti;
            _Td = Td;
        }

        public double GetControlVariable(double ActualTemp)
        {
            double error = ActualTemp - SetPoint;
            
            double P = GainP() * error;

            TimeSpan dT = DateTime.Now - _lastUpdate;
            _integral += error * dT.Seconds;
            _integral = _integral.Clamp(-1 * IntegralMax(), IntegralMax());
            double I = GainI() * _integral;

            double derivative = (ActualTemp - _lastActualTemp) / dT.Seconds;
            double D = GainD() * derivative;

            double u = P + I + D;
            Debug.WriteLine($"u={u} ({P}+{I}+{D})");

            _lastActualTemp = ActualTemp;
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
        /// Calculates a max integral value to prevent
        /// integral taking over.
        /// </summary>
        /// <see cref="https://github.com/DBorello/PiSmoker/issues/2#issuecomment-507793461" />
        private double IntegralMax()
        {
            return Math.Abs(0.5 / GainI());
        }
    }
}
