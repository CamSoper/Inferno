using Inferno.Common.Models;
using Inferno.Common.Proxies;
using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace Inferno.Cli
{
    class Program
    {
        static SmokerProxy _smokerProxy = new SmokerProxy();
        
        static async Task Main(string[] args)
        {
            if(args == null || args.Length < 1)
            {
                PrintHelp();
                return;
            }

            SmokerCommand command;
            if (!Enum.TryParse(args[0], true, out command))
            {
                PrintHelp();
                return;
            }

            switch(command)
            {
                case SmokerCommand.hold:
                case SmokerCommand.preheat:
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
                        if (command == SmokerCommand.hold)
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

                case SmokerCommand.p:
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

                case SmokerCommand.shutdown:
                    await ShutdownMode();
                    await PrintStatus();
                    break;

                case SmokerCommand.smoke:
                    await SmokeMode();
                    await PrintStatus();
                    break;

                case SmokerCommand.reset:
                    await Reset();
                    await PrintStatus();
                    break;

                case SmokerCommand.status:
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

            _smokerProxy.Dispose();
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
            await _smokerProxy.SetMode(SmokerMode.Smoke);
        }

        static async Task Reset()
        {
            if (Confirm())
            {
                await _smokerProxy.SetMode(SmokerMode.Shutdown);
                await _smokerProxy.SetMode(SmokerMode.Ready);
            }
        }

        static bool Confirm()
        {
            Console.WriteLine("Are you sure? (y/n)");
            var response = Console.ReadLine() ?? string.Empty;
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
            await _smokerProxy.SetMode(SmokerMode.Hold);
            await _smokerProxy.SetSetPoint(setPoint);
        }

        static async Task PreheatMode(int setPoint)
        {
            
            await _smokerProxy.SetSetPoint(setPoint);
        }

        static async Task HandlePCommand(int? pValue = null)
        {
            if (pValue == null)
            {
                var currentP = await _smokerProxy.GetPValue();
                Console.WriteLine($"P-{currentP}");
            }
            else
            {
                await _smokerProxy.SetPValue(pValue.Value);
            }
        }

        static async Task ShutdownMode()
        {
            if (Confirm())
            {
                await _smokerProxy.SetMode(SmokerMode.Shutdown);
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
            SmokerStatus status = await _smokerProxy.GetStatus();

            Console.WriteLine($"Mode: {status.Mode}");
            Console.WriteLine($"Setpoint: {status.SetPoint}°F");
            Console.WriteLine();
            Console.WriteLine($"Grill temp: {status.Temps?.GrillTemp}°F");
            Console.WriteLine($"Probe temp: " + ((status.Temps?.ProbeTemp > 0) ? status.Temps.ProbeTemp.ToString() + "°F" : "Unplugged"));
            Console.WriteLine();
            Console.Write($"🪵:{((status.AugerOn) ? "✅" : "❌")}|");
            Console.Write($"🔥:{((status.IgniterOn) ? "✅" : "❌")}|");
            Console.Write($"💨:{((status.BlowerOn) ? "✅" : "❌")}|");
            Console.Write($"🚨:{((status.FireHealthy) ? "🙂" : "😮")}");
            Console.WriteLine();
        }
    }
}
