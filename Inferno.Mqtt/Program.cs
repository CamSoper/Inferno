using Inferno.Mqtt.Services;

while (true)
{
    try
    {
        Console.WriteLine($"{DateTime.Now} Starting SmokerBridge");
        using var smokerBridge = await SmokerBridge.CreateAsync();
        Thread.Sleep(Timeout.InfiniteTimeSpan);
    }
    catch(Exception ex)
    {
        Console.WriteLine($"{DateTime.Now} WTF: {ex.Message}");
        Thread.Sleep(5000);
    }
}