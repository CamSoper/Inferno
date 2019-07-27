using Microsoft.Extensions.Configuration;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Inferno.RelayListener
{
    class Program
    {
        private static readonly string APPSETTINGS_FILENAME = "appsettings.json";

        static async Task Main(string[] args)
        {
            Console.WriteLine("Inferno cloud listener starting.");
            IConfiguration configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory()) // Directory where the json files are located
                .AddJsonFile(APPSETTINGS_FILENAME, optional: false, reloadOnChange: true)
                .Build();

            string connectionString = configuration.GetValue<string>("RelayConnectionString");

            Uri targetUri = new Uri("http://localhost:5000/api/");
            await RunAsync(connectionString, targetUri);

            return;
        }

        static async Task RunAsync(string connectionString, Uri targetUri)
        {
            HybridConnectionReverseProxy hybridProxy;
            while (true)
            {
                try
                {
                    hybridProxy = new HybridConnectionReverseProxy(connectionString, targetUri);
                    await hybridProxy.OpenAsync(CancellationToken.None);
                    await Task.Delay(-1);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"{ex.Message} {ex.StackTrace}");
                    await Task.Delay(TimeSpan.FromSeconds(10));
                }
            }
        }
    }
}
