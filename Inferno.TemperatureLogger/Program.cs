using Inferno.Common.Models;
using Newtonsoft.Json;
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
                SmokerStatus status = JsonConvert.DeserializeObject<SmokerStatus>(await _client.GetStringAsync("http://localhost:5000/api/status"));
                Console.WriteLine($"{status.CurrentTime},{status.SetPoint},{status.Temps.GrillTemp},{status.Temps.ProbeTemp}");
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        }
    }
}
