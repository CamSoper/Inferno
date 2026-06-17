namespace Inferno.Api.Models
{
    /// <summary>
    /// A cook session: the span from entering a cooking mode until returning to Ready.
    /// <see cref="EndTime"/> is null while the session is still active. Timestamps are UTC.
    /// </summary>
    public class CookSessionDto
    {
        public long Id { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public string? Label { get; set; }
        public double? PeakGrillTemp { get; set; }
        public double? PeakProbeTemp { get; set; }
        public int SampleCount { get; set; }
    }
}
