using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Inferno.Cli
{
    class Program
    {

        static async Task Main(string[] args)
        {
            if(args == null || args.Length < 1)
            {
                PrintHelp();
                return;
            }

            Command command;
            if (!Enum.TryParse(args[0], true, out command))
            {
                PrintHelp();
                return;
            }

            switch(command)
            {
                case Command.hold:

                    if (args.Length < 2)
                    {
                        PrintHelp();
                        return;
                    }
                    int setPoint;
                    if(!int.TryParse(args[1], out setPoint))
                    {
                        PrintHelp();
                        return;
                    }
                    else
                    {
                        await HoldMode(setPoint);
                        await PrintStatus();
                    }
                    break;

                case Command.smoke:
                    await SmokeMode();
                    await PrintStatus();
                    break;

                case Command.shutdown:
                    await ShutdownMode();
                    await PrintStatus();
                    break;

                case Command.status:
                    if(args.Length == 1)
                    {
                        await PrintStatus();
                    }
                    else if(args[1].ToLower() == "loop")
                    {
                        await PrintStatus(true);
                    }
                    else
                    {
                        PrintHelp();
                    }
                    break;
            }
        }

        static void PrintHelp()
        {
            Console.WriteLine("USAGE:");
            Console.WriteLine("");
            Console.WriteLine("Smoke mode: inferno smoke");
            Console.WriteLine("Hold mode: inferno hold nnn (where nnn is a temperature)");
            Console.WriteLine("Shutdown mode: inferno shutdown");
            Console.WriteLine("Display status: inferno status");
            Console.WriteLine("Continually display status: inferno status loop");
        }

        static async Task SmokeMode()
        {
            await InfernoApiRequest(Endpoint.mode, "\"smoke\"");
        }

        static async Task HoldMode(int setPoint)
        {
            await InfernoApiRequest(Endpoint.mode, "\"hold\"");
            await InfernoApiRequest(Endpoint.setpoint, setPoint.ToString());
        }

        static async Task ShutdownMode()
        {
            await InfernoApiRequest(Endpoint.mode, "\"shutdown\"");
        }

        static async Task PrintStatus(bool loop)
        {
            if (loop)
            {
                while (true)
                {
                    Console.Clear();
                    await PrintStatus();
                    await Task.Delay(TimeSpan.FromSeconds(3));
                }
            }
            else
            {
                await PrintStatus();
            }
        }

        static async Task PrintStatus()
        {
            HttpResponseMessage result = await InfernoApiRequest(Endpoint.status);
            JsonElement status =  JsonDocument.Parse(await result.Content.ReadAsStringAsync()).RootElement;

            double grill = status.GetProperty("temps").GetProperty("grillTemp").GetDouble();
            double probe = status.GetProperty("temps").GetProperty("probeTemp").GetDouble();
            bool augerOn = status.GetProperty("augerOn").GetBoolean();
            bool blowerOn = status.GetProperty("blowerOn").GetBoolean();
            bool igniterOn = status.GetProperty("igniterOn").GetBoolean();
            bool fireHealthy = status.GetProperty("fireHealthy").GetBoolean();
            int setPoint = status.GetProperty("setPoint").GetInt32();
            string mode = status.GetProperty("mode").GetString();

            Console.WriteLine($"Mode: {mode}");
            Console.WriteLine($"Setpoint: {setPoint}");
            Console.WriteLine();
            Console.WriteLine($"Grill temp: {grill}*F");
            Console.WriteLine($"Probe temp: " + ((probe > 0) ? probe.ToString() + "*F" : "Unplugged"));
            Console.WriteLine();
            Console.WriteLine($"Auger on: {augerOn}");
            Console.WriteLine($"Igniter on: {igniterOn}");
            Console.WriteLine($"Blower on: {blowerOn}");
            Console.WriteLine();
            Console.WriteLine($"Fire healthy: {fireHealthy}");
        }

        static async Task<HttpResponseMessage> InfernoApiRequest(Endpoint endpoint, string content = "")
        {
            Uri requestUri = new Uri($"http://localhost:5000/api/{endpoint}");
            HttpClient client = new HttpClient();
            HttpResponseMessage result;

            if (endpoint == Endpoint.status)
            {
                result = await client.GetAsync(requestUri);
            }
            else
            {
                HttpContent requestBody = new StringContent($"{content}", Encoding.UTF8, "application/json");
                result = await client.PostAsync(requestUri, requestBody);
            }
            result.EnsureSuccessStatusCode();
            return result;
        }
        enum Endpoint
        {
            mode,
            setpoint,
            status
        }

        enum Command
        {
            smoke,
            hold,
            shutdown,
            status
        }
    }
}
