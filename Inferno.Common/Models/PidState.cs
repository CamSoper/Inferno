using System;

namespace Inferno.Common.Models
{
    /// <summary>
    /// The PID controller's most recent Hold-mode decision, for tuning. Terms are in
    /// duty-cycle units (U = P + I + D, before clamping); Duty is what the auger got.
    /// </summary>
    public class PidState
    {
        public DateTime Timestamp { get; set; }
        public double SetPoint { get; set; }
        public double GrillTemp { get; set; }
        public double Error { get; set; }
        public double P { get; set; }
        public double I { get; set; }
        public double D { get; set; }
        public double U { get; set; }
        public double Duty { get; set; }
    }
}
