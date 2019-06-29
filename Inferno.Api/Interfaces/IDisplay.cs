namespace Inferno.Api.Interfaces
{
    public interface IDisplay
    {
         void DisplayInfo(double grillTemp, double probeTemp, string status, bool isIgniterOn = false);

         void DisplayText(string line1, string line2, string line3, string line4);
    }
}