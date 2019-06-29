using Inferno.Api.Models;

namespace Inferno.Api.Interfaces
{
    public interface ISmoker
    {
        SmokerMode Mode { get; }
        int SetPoint { get; set; }
        int PValue { get; set; }
        Temps Temps { get; }
        bool SetMode(SmokerMode mode);
    }
}