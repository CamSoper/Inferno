using System.Text;
using Inferno.Common.Models;
using Inferno.Common.Proxies;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;

namespace Inferno.Mqtt.Services
{
    public sealed class SmokerBridge : IDisposable
    {

        private const string TOPIC_ROOT = "inferno";
        private const string TOPIC_COMMAND = "command";
        private const string TOPIC_STATE = "state";
        private const string TOPIC_MODE = "mode";
        private const string TOPIC_SETPOINT = "setpoint";
        private const string TOPIC_PVALUE = "pvalue";
        private const string TOPIC_GRILLTEMP = "grill";
        private const string TOPIC_PROBETEMP = "probe";
        private const string TOPIC_AUGER = "auger";
        private const string TOPIC_BLOWER = "blower";
        private const string TOPIC_IGNITER = "igniter";
        private const string TOPIC_FIREHEALTHY = "firehealthy";


        private readonly string _brokerAddress;
        private readonly string _brokerUsername;
        private readonly string _brokerPassword;

        private IManagedMqttClient _mqttClient = null!;
        private Task _stateLoop = null!;
        private SmokerStatus _lastStatus = null!;
        private double _lastGrillTemp = 0;
        private double _lastProbeTemp = 0;

        private readonly SmokerProxy _proxy;

        private bool disposedValue;
    

        public SmokerBridge()
        {
            _brokerAddress = Environment.GetEnvironmentVariable("MQTT_BROKER_ADDRESS") ?? "localhost";
            _brokerUsername = Environment.GetEnvironmentVariable("MQTT_USERNAME") ?? "";
            _brokerPassword = Environment.GetEnvironmentVariable("MQTT_PASSWORD") ?? "";

            Console.WriteLine($"Broker Address: {_brokerAddress}");
            Console.WriteLine($"Broker Username: {_brokerUsername}");

            _proxy = new SmokerProxy();
        }

        public static async Task<SmokerBridge> CreateAsync()
        {
            var smokerBridge = new SmokerBridge();
            await smokerBridge.InitializeAsync();
            return smokerBridge;
        }

        private async Task InitializeAsync()
        {
            Console.WriteLine($"{DateTime.Now} Initializing SmokerBridge");

            var mqttFactory = new MqttFactory();
            _mqttClient = mqttFactory.CreateManagedMqttClient();

            var mqttClientOptions = new MqttClientOptionsBuilder()
                .WithTcpServer(_brokerAddress)
                .WithCredentials(_brokerUsername, _brokerPassword)
                .Build();

            var managedMqttClientOptions = new ManagedMqttClientOptionsBuilder()
                .WithClientOptions(mqttClientOptions)
                .Build();

            await _mqttClient.StartAsync(managedMqttClientOptions);

            var mqttSubscribeOptions = mqttFactory.CreateSubscribeOptionsBuilder()
                .WithTopicFilter(
                    f =>
                    {
                        f.WithTopic(GetCommandTopic(TOPIC_MODE));
                    })
                .WithTopicFilter(
                    f =>
                    {
                        f.WithTopic(GetCommandTopic(TOPIC_SETPOINT));
                    })
                .WithTopicFilter(
                    f =>
                    {
                        f.WithTopic(GetCommandTopic(TOPIC_PVALUE));
                    })
                .Build();

            await _mqttClient.SubscribeAsync(mqttSubscribeOptions.TopicFilters);

            _mqttClient.ApplicationMessageReceivedAsync += ProcessCommand;

            _stateLoop = StateLoop();
        }

        private async Task ProcessCommand(MqttApplicationMessageReceivedEventArgs args)
        {
            var topic = args.ApplicationMessage.Topic;
            var payload = Encoding.UTF8.GetString(args.ApplicationMessage.Payload);
            Console.WriteLine($"{DateTime.Now} Received on {topic}: {payload}");

            if (topic == SmokerBridge.GetCommandTopic(TOPIC_MODE))
            {
                if (Enum.TryParse(payload, true, out SmokerMode mode))
                {
                    await _proxy.SetModeAsync(mode);
                }
            }
            else if (topic == SmokerBridge.GetCommandTopic(TOPIC_SETPOINT))
            {
                if (int.TryParse(payload, out var setPoint))
                {
                    await _proxy.SetSetPointAsync(setPoint);
                }
            }
            else if (topic == SmokerBridge.GetCommandTopic(TOPIC_PVALUE))
            {
                if (int.TryParse(payload, out var pValue))
                {
                    await _proxy.SetPValueAsync(pValue);
                }
            }
        }

        private static string GetCommandTopic(string topic)
        {
            return $"{TOPIC_ROOT}/{topic}/{TOPIC_COMMAND}";
        }

        private static string GetStateTopic(string topic)
        {
            return $"{TOPIC_ROOT}/{topic}/{TOPIC_STATE}";
        }

        private async Task StateLoop()
        {
            Console.WriteLine($"{DateTime.Now} Starting StateLoop");

            int iteration = 0;

            while (true)
            {
                try
                {
                    var status = await _proxy.GetStatusAsync();
                    bool forceUpdate = false;
                    if (_lastStatus is null)
                    {
                        _lastStatus = status;
                        forceUpdate = true;
                    }

                    await SendUpdateMessage(status.AugerOn.ToString(),
                                            _lastStatus.AugerOn.ToString(),
                                            TOPIC_AUGER,
                                            forceUpdate);

                    await SendUpdateMessage(status.BlowerOn.ToString(),
                                            _lastStatus.BlowerOn.ToString(),
                                            TOPIC_BLOWER,
                                            forceUpdate);

                    await SendUpdateMessage(status.IgniterOn.ToString(),
                                            _lastStatus.IgniterOn.ToString(),
                                            TOPIC_IGNITER,
                                            forceUpdate);

                    await SendUpdateMessage(status.FireHealthy.ToString(),
                                            _lastStatus.FireHealthy.ToString(),
                                            TOPIC_FIREHEALTHY,
                                            forceUpdate);

                    await SendUpdateMessage(status.Mode,
                                            _lastStatus.Mode,
                                            TOPIC_MODE,
                                            forceUpdate);

                    await SendUpdateMessage(status.SetPoint.ToString(),
                                            _lastStatus.SetPoint.ToString(),
                                            TOPIC_SETPOINT,
                                            forceUpdate);

                    await SendUpdateMessage(status.PValue.ToString(),
                                            _lastStatus.PValue.ToString(),
                                            TOPIC_PVALUE,
                                            forceUpdate);

                    // Only update the grill/probe temps every 5 iterations
                    if (iteration == 0 && status.Temps is not null)
                    {
                        if (_lastGrillTemp != status.Temps?.GrillTemp)
                        {
                            double grillTemp = status.Temps?.GrillTemp ?? -1;
                            await SendUpdateMessage(grillTemp.ToString(),
                                                    _lastGrillTemp.ToString(),
                                                    TOPIC_GRILLTEMP,
                                                    forceUpdate);
                            _lastGrillTemp = grillTemp;
                        }
                        
                        if(_lastProbeTemp != status.Temps?.ProbeTemp)
                        {
                            double probeTemp = status.Temps?.ProbeTemp ?? -1;
                            await SendUpdateMessage(probeTemp.ToString(),
                                                _lastProbeTemp.ToString(),
                                                TOPIC_PROBETEMP,
                                                forceUpdate);
                            _lastProbeTemp = probeTemp;
                        }
                    }

                    _lastStatus = status;
                    
                    if(iteration == 4)
                    {
                        iteration = 0;
                    }
                    else
                    {
                        iteration++;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"{DateTime.Now} Error in StateLoop: {ex}");
                    iteration = 0;
                }
                
                await Task.Delay(1000);
            }
        }

        private async Task SendUpdateMessage(string currentValue, string lastValue, string topic, bool forceUpdate = false)
        {
            if (lastValue != currentValue || forceUpdate)
            {
                var mqttMessage = new MqttApplicationMessageBuilder()
                    .WithTopic(GetStateTopic(topic))
                    .WithPayload(currentValue)
                    .WithRetainFlag()
                    .Build();

                await _mqttClient.EnqueueAsync(mqttMessage);
            }
        }

        private void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    _mqttClient.Dispose();
                }

                // TODO: free unmanaged resources (unmanaged objects) and override finalizer
                // TODO: set large fields to null
                disposedValue = true;
            }
        }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}