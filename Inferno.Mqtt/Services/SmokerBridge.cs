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

        private readonly MqttSettings _settings;

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


        public SmokerBridge(MqttSettings settings, string apiBaseUrl)
        {
            _settings = settings;

            _brokerAddress = settings.BrokerAddress;
            _brokerUsername = settings.BrokerUsername;
            _brokerPassword = settings.BrokerPassword;

            Console.WriteLine($"Broker Address: {_brokerAddress}");
            Console.WriteLine($"Broker Username: {_brokerUsername}");

            _proxy = new SmokerProxy(apiBaseUrl);
        }

        public static async Task<SmokerBridge> CreateAsync(MqttSettings settings, string apiBaseUrl)
        {
            var smokerBridge = new SmokerBridge(settings, apiBaseUrl);
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
                        f.WithTopic(GetCommandTopic(_settings.TopicMode));
                    })
                .WithTopicFilter(
                    f =>
                    {
                        f.WithTopic(GetCommandTopic(_settings.TopicSetPoint));
                    })
                .WithTopicFilter(
                    f =>
                    {
                        f.WithTopic(GetCommandTopic(_settings.TopicPValue));
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

            if (topic == GetCommandTopic(_settings.TopicMode))
            {
                if (Enum.TryParse(payload, true, out SmokerMode mode))
                {
                    await _proxy.SetModeAsync(mode);
                }
            }
            else if (topic == GetCommandTopic(_settings.TopicSetPoint))
            {
                if (int.TryParse(payload, out var setPoint))
                {
                    await _proxy.SetSetPointAsync(setPoint);
                }
            }
            else if (topic == GetCommandTopic(_settings.TopicPValue))
            {
                if (int.TryParse(payload, out var pValue))
                {
                    await _proxy.SetPValueAsync(pValue);
                }
            }
        }

        private string GetCommandTopic(string topic)
        {
            return $"{_settings.TopicRoot}/{topic}/{_settings.TopicCommand}";
        }

        private string GetStateTopic(string topic)
        {
            return $"{_settings.TopicRoot}/{topic}/{_settings.TopicState}";
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
                                            _settings.TopicAuger,
                                            forceUpdate);

                    await SendUpdateMessage(status.BlowerOn.ToString(),
                                            _lastStatus.BlowerOn.ToString(),
                                            _settings.TopicBlower,
                                            forceUpdate);

                    await SendUpdateMessage(status.IgniterOn.ToString(),
                                            _lastStatus.IgniterOn.ToString(),
                                            _settings.TopicIgniter,
                                            forceUpdate);

                    await SendUpdateMessage(status.FireHealthy.ToString(),
                                            _lastStatus.FireHealthy.ToString(),
                                            _settings.TopicFireHealthy,
                                            forceUpdate);

                    await SendUpdateMessage(status.Mode,
                                            _lastStatus.Mode,
                                            _settings.TopicMode,
                                            forceUpdate);

                    await SendUpdateMessage(status.SetPoint.ToString(),
                                            _lastStatus.SetPoint.ToString(),
                                            _settings.TopicSetPoint,
                                            forceUpdate);

                    await SendUpdateMessage(status.PValue.ToString(),
                                            _lastStatus.PValue.ToString(),
                                            _settings.TopicPValue,
                                            forceUpdate);

                    // Only update the grill/probe temps every 5 iterations
                    if (iteration == 0 && status.Temps is not null)
                    {
                        if (_lastGrillTemp != status.Temps?.GrillTemp)
                        {
                            double grillTemp = status.Temps?.GrillTemp ?? -1;
                            await SendUpdateMessage(grillTemp.ToString(),
                                                    _lastGrillTemp.ToString(),
                                                    _settings.TopicGrillTemp,
                                                    forceUpdate);
                            _lastGrillTemp = grillTemp;
                        }
                        
                        if(_lastProbeTemp != status.Temps?.ProbeTemp)
                        {
                            double probeTemp = status.Temps?.ProbeTemp ?? -1;
                            await SendUpdateMessage(probeTemp.ToString(),
                                                _lastProbeTemp.ToString(),
                                                _settings.TopicProbeTemp,
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