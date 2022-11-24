using System;

namespace Inferno.Common.Models
{
    public class SmokerStatus
    {
        public bool AugerOn { get; set; }
        public bool BlowerOn { get; set; }
        public bool IgniterOn { get; set; }
        public Temps? Temps { get; set; }
        public bool FireHealthy { get; set; }
        public string Mode { get; set; } = "";
        public int SetPoint { get; set; }
        public int PValue { get; set; }
        public DateTime ModeTime { get; set; }
        public DateTime CurrentTime { get; set; }
    }
}