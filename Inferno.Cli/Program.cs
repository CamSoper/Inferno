using Inferno.Common.Models;
using Newtonsoft.Json;
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
                case Command.preheat:
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
                        if (command == Command.hold)
                        {
                            await HoldMode(setPoint);
                        }
                        else
                        {
                            await PreheatMode(setPoint);
                        }
                        await PrintStatus();
                    }
                    break;

                case Command.p:
                    if (args.Length > 1)
                    {
                        int pValue;
                        if (!int.TryParse(args[1], out pValue))
                        {
                            PrintHelp();
                            return;
                        }
                        else
                        {
                            await HandlePCommand(pValue);
                        }
                    }
                    await HandlePCommand(null);
                    break;

                case Command.shutdown:
                    await ShutdownMode();
                    await PrintStatus();
                    break;

                case Command.smoke:
                    await SmokeMode();
                    await PrintStatus();
                    break;

                case Command.reset:
                    await Reset();
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
            Console.WriteLine("");
            Console.WriteLine("USAGE:");
            Console.WriteLine("");
            Console.WriteLine("Smoke mode: inferno smoke");
            Console.WriteLine("Hold mode: inferno hold nnn (where nnn is a temperature)");
            Console.WriteLine("Preheat mode: inferno preheat nnn (where nnn is a temperature)");
            Console.WriteLine("Shutdown mode: inferno shutdown");
            Console.WriteLine("Force ready mode: inferno reset");
            Console.WriteLine("Display P-Value: inferno p");
            Console.WriteLine("Adjust P-Value: inferno p n (where nnn is an integer)");
            Console.WriteLine("Display status: inferno status");
            Console.WriteLine("Continually display status: inferno status loop");
        }

        static async Task SmokeMode()
        {
            await SetMode(SmokerMode.Smoke);
        }

        static async Task Reset()
        {
            if (Confirm())
            {
                await SetMode(SmokerMode.Ready);
            }
        }

        static bool Confirm()
        {
            Console.WriteLine("Are you sure? (y/n)");
            var response = Console.ReadLine();
            if(response.Length < 1 ||
                (!response.StartsWith("y", true, null) && 
                    !response.StartsWith("n", true, null)))
            {
                return Confirm();
            }

            return response.StartsWith("y", true, null);
        }

        static async Task HoldMode(int setPoint)
        {
            await SetMode(SmokerMode.Hold);
            await InfernoApiRequest(Endpoint.setpoint, setPoint.ToString());
        }

        static async Task PreheatMode(int setPoint)
        {
            
            await InfernoApiRequest(Endpoint.setpoint, setPoint.ToString());
        }

        static async Task HandlePCommand(int? pValue = null)
        {
            if (pValue == null)
            {
                HttpResponseMessage result = await InfernoApiRequest(Endpoint.pvalue);
                string currentP = await result.Content.ReadAsStringAsync();
                Console.WriteLine($"P-{currentP}");
            }
            else
            {
                await InfernoApiRequest(Endpoint.pvalue, $"{pValue}");
            }
        }

        static async Task SetMode(SmokerMode smokerMode)
        {
            await InfernoApiRequest(Endpoint.mode, $"\"{smokerMode}\"");
        }

        static async Task ShutdownMode()
        {
            if (Confirm())
            {
                await SetMode(SmokerMode.Shutdown);
            }
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

            SmokerStatus status = JsonConvert.DeserializeObject<SmokerStatus>(await result.Content.ReadAsStringAsync());

            Console.WriteLine($"Mode: {status.Mode}");
            Console.WriteLine($"Setpoint: {status.SetPoint}");
            Console.WriteLine();
            Console.WriteLine($"Grill temp: {status.Temps.GrillTemp}*F");
            Console.WriteLine($"Probe temp: " + ((status.Temps.ProbeTemp > 0) ? status.Temps.ProbeTemp.ToString() + "*F" : "Unplugged"));
            Console.WriteLine();
            Console.WriteLine($"Auger on: {status.AugerOn}");
            Console.WriteLine($"Igniter on: {status.IgniterOn}");
            Console.WriteLine($"Blower on: {status.BlowerOn}");
            Console.WriteLine();
            Console.WriteLine($"Fire healthy: {status.FireHealthy}");
        }

        static async Task<HttpResponseMessage> InfernoApiRequest(Endpoint endpoint, string content = "")
        {
            Uri requestUri = new Uri($"http://localhost:5000/api/{endpoint}");
            HttpClient client = new HttpClient();
            HttpResponseMessage result;

            if (string.IsNullOrEmpty(content))
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
            status,
            pvalue
        }

        enum Command
        {
            smoke,
            hold,
            shutdown,
            status,
            reset,
            p,
            preheat
        }
    }
}
