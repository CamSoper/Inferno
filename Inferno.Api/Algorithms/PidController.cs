using System;
using System.Diagnostics;

namespace Inferno.Api.Algorithms
{
    public class PidController
    {
        double _PB;
        double _Ti;
        double _Td;

        double _integral;

        DateTime _lastUpdate;
        double _lastProcessVariable;

        public double SetPoint { get; private set;}
        public PidController(double PB, double Ti, double Td)
        {
            _PB = PB;
            _Ti = Ti;
            _Td = Td;

            SetPoint = 150;
            _lastProcessVariable = 180;
        }

        public double GetControlVariable(double CurrentProcessVariable)
        {
            double error = CurrentProcessVariable - SetPoint;
            if (Math.Abs(error) <= 1)
            {
                // If we're within 1 degree, it's good enough.
                error = 0;
            }
            
            double P = Kp() * error + 0.5;

            TimeSpan dT = DateTime.Now - _lastUpdate;
            _integral += error * dT.Seconds;
            _integral = Math.Max(_integral, -1 * IntegralMax());
            _integral = Math.Min(_integral, IntegralMax());
            double I = Ki() * _integral;

            double derivative = (CurrentProcessVariable - _lastProcessVariable);
            double D = Kd() * derivative;

            double u = P + I + D;
            Debug.WriteLine($"u={u} ({P}+{I}+{D})");

            _lastProcessVariable = CurrentProcessVariable;
            _lastUpdate = DateTime.Now;

            return u;
        }


        private double Kp()
        {
            return -1 / _PB;
        }

        private double Ki()
        {
            return Kp() / _Ti; 
        }

        private double Kd()
        {
            return Kp() * _Td;
        }

        private double IntegralMax()
        {
            return Math.Abs(0.5 / Ki());
        }
        public void SetNewSetpoint(double setPoint)
        {
            SetPoint = setPoint;
            _lastUpdate = DateTime.Now;
        }
    }
}
