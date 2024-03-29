using Inferno.Common.Models;

namespace Inferno.Common.Interfaces
{
    public interface ISmoker
    {
        SmokerMode Mode { get; }
        SmokerStatus Status { get; }
        int SetPoint { get; set; }
        int PValue { get; set; }
        Temps Temps { get; }
        bool SetMode(SmokerMode mode);
    }
}