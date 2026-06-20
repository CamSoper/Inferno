namespace Inferno.Api.Models
{
    /// <summary>
    /// A single point-in-time snapshot of the smoker, captured by <see cref="Services.CookLogger"/>
    /// and persisted as one row in the <c>sample</c> table. Timestamps are UTC.
    /// </summary>
    public class CookSample
    {
        public DateTime Timestamp { get; set; }
        public double GrillTemp { get; set; }
        public double ProbeTemp { get; set; }
        public string Mode { get; set; } = "";
        public int SetPoint { get; set; }
        public int PValue { get; set; }
        public bool AugerOn { get; set; }
        public bool BlowerOn { get; set; }
        public bool IgniterOn { get; set; }
        public bool FireHealthy { get; set; }
        public bool Preheated { get; set; }
    }
}
