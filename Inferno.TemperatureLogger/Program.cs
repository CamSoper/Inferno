using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Inferno.TemperatureLogger
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("Timestamp,Setpoint,Grill,Probe");
            HttpClient _client = new HttpClient();

            while (true)
            {
                JsonDocument status = JsonDocument.Parse(await _client.GetStringAsync("http://localhost:5000/api/status"));
                DateTime timeStamp = status.RootElement.GetProperty("currentTime").GetDateTime();
                int setPoint = status.RootElement.GetProperty("setPoint").GetInt32();
                double grill = status.RootElement.GetProperty("temps").GetProperty("grillTemp").GetDouble();
                double probe = status.RootElement.GetProperty("temps").GetProperty("probeTemp").GetDouble();

                Console.WriteLine($"{timeStamp},{setPoint},{grill},{probe}");
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        }
    }
}
