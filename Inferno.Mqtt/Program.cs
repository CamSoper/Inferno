using Inferno.Mqtt;
using Inferno.Mqtt.Services;
using Microsoft.Extensions.Configuration;

var configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: false)
    .AddEnvironmentVariables()
    .Build();

var mqttSettings = configuration.GetSection("Mqtt").Get<MqttSettings>() ?? new MqttSettings();
var apiBaseUrl = configuration["Api:BaseUrl"] ?? "http://127.0.0.1:5000";

while (true)
{
    try
    {
        Console.WriteLine($"{DateTime.Now} Starting SmokerBridge");
        using var smokerBridge = await SmokerBridge.CreateAsync(mqttSettings, apiBaseUrl);
        Thread.Sleep(Timeout.InfiniteTimeSpan);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"{DateTime.Now} WTF: {ex.Message}");
        Thread.Sleep(5000);
    }
}
