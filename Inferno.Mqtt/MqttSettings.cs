namespace Inferno.Mqtt;

public class MqttSettings
{
    public string BrokerAddress { get; set; } = "localhost";
    public string BrokerUsername { get; set; } = "";
    public string BrokerPassword { get; set; } = "";
    public string TopicRoot { get; set; } = "inferno";
    public string TopicCommand { get; set; } = "command";
    public string TopicState { get; set; } = "state";
    public string TopicMode { get; set; } = "mode";
    public string TopicSetPoint { get; set; } = "setpoint";
    public string TopicPValue { get; set; } = "pvalue";
    public string TopicGrillTemp { get; set; } = "grill";
    public string TopicProbeTemp { get; set; } = "probe";
    public string TopicAuger { get; set; } = "auger";
    public string TopicBlower { get; set; } = "blower";
    public string TopicIgniter { get; set; } = "igniter";
    public string TopicFireHealthy { get; set; } = "firehealthy";
}
