namespace Inferno.Api.Interfaces
{
    public interface IDisplay
    {
         void DisplayInfo(double grillTemp, double probeTemp, string mode, string hardwareStatus);

         void DisplayText(string line1, string line2, string line3, string line4);
    }
}