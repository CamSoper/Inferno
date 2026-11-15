using System.Text;
using System.Text.Json;
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
        private const string TOPIC_PREHEATED = "preheated";

        // Telemetry. The bridge's MQTT last will flips AVAILABILITY to "offline" if the
        // Pi dies or drops off the network; API_AVAILABILITY tracks whether the API
        // answers. Smoker entities need both; diagnostic entities need only the first.
        internal const string TOPIC_AVAILABILITY = "inferno/availability";
        internal const string TOPIC_API_AVAILABILITY = "inferno/api/availability";
        internal const string TOPIC_DIAG = "inferno/diag/state";
        internal const string TOPIC_ALERT = "inferno/alert";
        private const string ONLINE = "online";
        private const string OFFLINE = "offline";

        private static readonly TimeSpan ThrottlePollInterval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan DiagPublishInterval = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan ApiDownAlertAfter = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan AlertCooldown = TimeSpan.FromMinutes(5);


        private readonly string _brokerAddress;
        private readonly string _brokerUsername;
        private readonly string _brokerPassword;

        private IManagedMqttClient _mqttClient = null!;
        private Task _stateLoop = null!;
        private SmokerStatus _lastStatus = null!;
        private double _lastGrillTemp = 0;
        private double _lastProbeTemp = 0;

        private readonly SmokerProxy _proxy;

        private readonly HealthMonitor _health;
        private Task _healthLoop = null!;
        private readonly CancellationTokenSource _stopCts = new();
        private DateTimeOffset? _lastApiSuccess;
        private bool? _apiAvailablePublished;
        private readonly Dictionary<string, DateTimeOffset> _lastAlert = new();

        private bool disposedValue;
    

        public SmokerBridge()
        {
            _brokerAddress = Environment.GetEnvironmentVariable("MQTT_BROKER_ADDRESS") ?? "localhost";
            _brokerUsername = Environment.GetEnvironmentVariable("MQTT_USERNAME") ?? "";
            _brokerPassword = Environment.GetEnvironmentVariable("MQTT_PASSWORD") ?? "";

            Console.WriteLine($"Broker Address: {_brokerAddress}");
            Console.WriteLine($"Broker Username: {_brokerUsername}");

            _proxy = new SmokerProxy();
            _health = new HealthMonitor(() => IsApiOk);
        }

        private bool IsApiOk =>
            _lastApiSuccess is { } t && DateTimeOffset.Now - t < TimeSpan.FromSeconds(10);

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
                // Broker declares us dead after ~1.5x keepalive without traffic.
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(15))
                .WithWillTopic(TOPIC_AVAILABILITY)
                .WithWillPayload(OFFLINE)
                .WithWillRetain(true)
                .Build();

            var managedMqttClientOptions = new ManagedMqttClientOptionsBuilder()
                .WithClientOptions(mqttClientOptions)
                .Build();

            // Announce "online" on every (re)connect; the will covers the opposite case.
            _mqttClient.ConnectedAsync += async _ =>
            {
                Console.WriteLine($"{DateTime.Now} MQTT connected; publishing availability");
                await PublishAsync(TOPIC_AVAILABILITY, ONLINE, retain: true);
            };

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

            await PublishDiscoveryAsync();
            await PublishDiagnosticsDiscoveryAsync();

            _stateLoop = StateLoop();
            _healthLoop = HealthLoop();
        }

        private async Task PublishDiscoveryAsync()
        {
            Console.WriteLine($"{DateTime.Now} Publishing HA MQTT discovery configs");

            var device = new Dictionary<string, object>
            {
                ["identifiers"] = new[] { "inferno_smoker" },
                ["name"] = "Inferno Smoker",
                ["manufacturer"] = "Traeger / DIY",
                ["model"] = "Junior Elite 20 (Modified)",
            };

            // Sensor entities
            await PublishDiscoveryConfigAsync("sensor", "grill_temp", new Dictionary<string, object>
            {
                ["name"] = "Grill Temperature",
                ["state_topic"] = GetStateTopic(TOPIC_GRILLTEMP),
                ["unit_of_measurement"] = "°F",
                ["device_class"] = "temperature",
                ["state_class"] = "measurement",
                ["device"] = device,
                ["unique_id"] = "inferno_grill_temp",
            });

            await PublishDiscoveryConfigAsync("sensor", "probe_temp", new Dictionary<string, object>
            {
                ["name"] = "Probe Temperature",
                ["state_topic"] = GetStateTopic(TOPIC_PROBETEMP),
                ["unit_of_measurement"] = "°F",
                ["device_class"] = "temperature",
                ["state_class"] = "measurement",
                ["device"] = device,
                ["unique_id"] = "inferno_probe_temp",
            });

            await PublishDiscoveryConfigAsync("sensor", "set_point_sensor", new Dictionary<string, object>
            {
                ["name"] = "Set Point",
                ["state_topic"] = GetStateTopic(TOPIC_SETPOINT),
                ["unit_of_measurement"] = "°F",
                ["icon"] = "mdi:thermometer-check",
                ["device"] = device,
                ["unique_id"] = "inferno_set_point_sensor",
            });

            await PublishDiscoveryConfigAsync("sensor", "p_value_sensor", new Dictionary<string, object>
            {
                ["name"] = "P-Value",
                ["state_topic"] = GetStateTopic(TOPIC_PVALUE),
                ["icon"] = "mdi:fire",
                ["device"] = device,
                ["unique_id"] = "inferno_p_value_sensor",
            });

            await PublishDiscoveryConfigAsync("sensor", "mode_sensor", new Dictionary<string, object>
            {
                ["name"] = "Mode",
                ["state_topic"] = GetStateTopic(TOPIC_MODE),
                ["icon"] = "mdi:grill",
                ["device"] = device,
                ["unique_id"] = "inferno_mode_sensor",
            });

            // Binary sensor entities
            await PublishDiscoveryConfigAsync("binary_sensor", "auger", new Dictionary<string, object>
            {
                ["name"] = "Auger",
                ["state_topic"] = GetStateTopic(TOPIC_AUGER),
                ["payload_on"] = "True",
                ["payload_off"] = "False",
                ["icon"] = "mdi:screw-lag",
                ["device"] = device,
                ["unique_id"] = "inferno_auger",
            });

            await PublishDiscoveryConfigAsync("binary_sensor", "blower", new Dictionary<string, object>
            {
                ["name"] = "Blower",
                ["state_topic"] = GetStateTopic(TOPIC_BLOWER),
                ["payload_on"] = "True",
                ["payload_off"] = "False",
                ["icon"] = "mdi:fan",
                ["device"] = device,
                ["unique_id"] = "inferno_blower",
            });

            await PublishDiscoveryConfigAsync("binary_sensor", "igniter", new Dictionary<string, object>
            {
                ["name"] = "Igniter",
                ["state_topic"] = GetStateTopic(TOPIC_IGNITER),
                ["payload_on"] = "True",
                ["payload_off"] = "False",
                ["icon"] = "mdi:fire-alert",
                ["device"] = device,
                ["unique_id"] = "inferno_igniter",
            });

            await PublishDiscoveryConfigAsync("binary_sensor", "fire_healthy", new Dictionary<string, object>
            {
                ["name"] = "Fire Healthy",
                ["state_topic"] = GetStateTopic(TOPIC_FIREHEALTHY),
                ["payload_on"] = "False",
                ["payload_off"] = "True",
                ["device_class"] = "problem",
                ["device"] = device,
                ["unique_id"] = "inferno_fire_healthy",
            });

            await PublishDiscoveryConfigAsync("binary_sensor", "preheated", new Dictionary<string, object>
            {
                ["name"] = "Preheated",
                ["state_topic"] = GetStateTopic(TOPIC_PREHEATED),
                ["payload_on"] = "True",
                ["payload_off"] = "False",
                ["icon"] = "mdi:thermometer-check",
                ["device"] = device,
                ["unique_id"] = "inferno_preheated",
            });

            // Control entities
            var modes = Enum.GetNames<SmokerMode>();
            await PublishDiscoveryConfigAsync("select", "mode", new Dictionary<string, object>
            {
                ["name"] = "Mode",
                ["state_topic"] = GetStateTopic(TOPIC_MODE),
                ["command_topic"] = GetCommandTopic(TOPIC_MODE),
                ["options"] = modes,
                ["icon"] = "mdi:grill",
                ["device"] = device,
                ["unique_id"] = "inferno_mode",
            });

            await PublishDiscoveryConfigAsync("number", "set_point", new Dictionary<string, object>
            {
                ["name"] = "Set Point",
                ["state_topic"] = GetStateTopic(TOPIC_SETPOINT),
                ["command_topic"] = GetCommandTopic(TOPIC_SETPOINT),
                ["min"] = 180,
                ["max"] = 400,
                ["step"] = 5,
                ["unit_of_measurement"] = "°F",
                ["icon"] = "mdi:thermometer-check",
                ["device"] = device,
                ["unique_id"] = "inferno_set_point",
            });

            await PublishDiscoveryConfigAsync("number", "p_value", new Dictionary<string, object>
            {
                ["name"] = "P-Value",
                ["state_topic"] = GetStateTopic(TOPIC_PVALUE),
                ["command_topic"] = GetCommandTopic(TOPIC_PVALUE),
                ["min"] = 0,
                ["max"] = 5,
                ["step"] = 1,
                ["icon"] = "mdi:fire",
                ["device"] = device,
                ["unique_id"] = "inferno_p_value",
            });

            Console.WriteLine($"{DateTime.Now} HA MQTT discovery configs published");
        }

        private async Task PublishDiscoveryConfigAsync(string component, string objectId, Dictionary<string, object> config, bool needsApi = true)
        {
            // Smoker entities go unavailable if either the bridge or the API is down;
            // diagnostic entities only need the bridge, so they keep reporting while
            // the API is wedged (which is exactly when you want them).
            if (!config.ContainsKey("availability"))
            {
                var availability = new List<Dictionary<string, object>>
                {
                    new() { ["topic"] = TOPIC_AVAILABILITY },
                };
                if (needsApi)
                {
                    availability.Add(new() { ["topic"] = TOPIC_API_AVAILABILITY });
                }
                config["availability"] = availability;
                config["availability_mode"] = "all";
            }

            var topic = $"homeassistant/{component}/inferno/{objectId}/config";
            var payload = JsonSerializer.Serialize(config);
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithRetainFlag()
                .Build();
            await _mqttClient.EnqueueAsync(message);
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
            int stateErrors = 0;

            while (true)
            {
                try
                {
                    var status = await _proxy.GetStatusAsync();
                    _lastApiSuccess = DateTimeOffset.Now;
                    if (stateErrors > 0)
                    {
                        Console.WriteLine($"{DateTime.Now} StateLoop recovered after {stateErrors} failed polls");
                        stateErrors = 0;
                    }
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

                    await SendUpdateMessage(status.Preheated.ToString(),
                                            _lastStatus.Preheated.ToString(),
                                            TOPIC_PREHEATED,
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
                    // Log the first failure in full, then one line a minute, so a dead
                    // API can't flood the journal (and the SD card) once a second.
                    if (stateErrors++ == 0)
                    {
                        Console.WriteLine($"{DateTime.Now} Error in StateLoop: {ex}");
                    }
                    else if (stateErrors % 60 == 0)
                    {
                        Console.WriteLine($"{DateTime.Now} StateLoop still failing ({stateErrors} polls): {ex.Message}");
                    }
                    iteration = 0;
                }
                
                await Task.Delay(1000);
            }
        }

        // ---- Telemetry ----

        private async Task HealthLoop()
        {
            Console.WriteLine($"{DateTime.Now} Starting HealthLoop");

            // Boot self-test: one loud alert up front if the Pi came up broken
            // (wiped config.txt, missing SPI/I2C, FAT repair debris).
            var first = _health.Collect();
            await PublishDiagAsync(first);
            if (!first.SelfTestOk)
            {
                await AlertAsync("self_test_failed", string.Join("; ", first.SelfTestFailures), first);
            }

            bool lastUndervoltage = first.UndervoltageNow == true;
            bool lastThrottled = first.ThrottledNow == true;

            var lastDiag = DateTimeOffset.Now;
            DateTimeOffset? apiDownSince = null;

            while (!_stopCts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(ThrottlePollInterval, _stopCts.Token);

                    await UpdateApiAvailabilityAsync();

                    var now = DateTimeOffset.Now;
                    apiDownSince = IsApiOk ? null : apiDownSince ?? now;

                    // Throttle flags are cheap to read, so poll them fast. Undervoltage
                    // typically shows up minutes before a brownout reboot, so an edge
                    // pushes a fresh snapshot right away. It's a signal for HA history,
                    // not an alert: a marginal PSU dips often without being an emergency.
                    var throttle = _health.ReadThrottle();
                    bool uvNow = throttle?.UndervoltageNow == true;
                    bool thrNow = throttle?.ThrottledNow == true;
                    bool edge = (uvNow && !lastUndervoltage) || (thrNow && !lastThrottled);

                    if (edge || now - lastDiag >= DiagPublishInterval)
                    {
                        var snapshot = _health.Collect(throttle);
                        await PublishDiagAsync(snapshot);
                        lastDiag = now;

                        if (thrNow && !lastThrottled)
                        {
                            await AlertAsync("throttled", "Firmware is throttling the CPU", snapshot);
                        }
                        if (apiDownSince is { } since && now - since >= ApiDownAlertAfter)
                        {
                            await AlertAsync("api_down", $"Inferno API not answering since {since:T}", snapshot);
                        }
                    }

                    lastUndervoltage = uvNow;
                    lastThrottled = thrNow;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"{DateTime.Now} Error in HealthLoop: {ex.Message}");
                }
            }
        }

        private async Task UpdateApiAvailabilityAsync()
        {
            bool ok = IsApiOk;
            if (_apiAvailablePublished != ok)
            {
                await PublishAsync(TOPIC_API_AVAILABILITY, ok ? ONLINE : OFFLINE, retain: true);
                _apiAvailablePublished = ok;
            }
        }

        private Task PublishDiagAsync(HealthSnapshot snapshot) =>
            PublishAsync(TOPIC_DIAG, JsonSerializer.Serialize(snapshot), retain: true);

        /// <summary>
        /// Publishes a non-retained alert with the current snapshot attached, at most
        /// once per <see cref="AlertCooldown"/> per kind so a flapping PSU can't spam.
        /// </summary>
        private async Task AlertAsync(string kind, string message, HealthSnapshot snapshot)
        {
            var now = DateTimeOffset.Now;
            if (_lastAlert.TryGetValue(kind, out var last) && now - last < AlertCooldown)
            {
                return;
            }
            _lastAlert[kind] = now;

            Console.WriteLine($"{DateTime.Now} ALERT {kind}: {message}");
            var payload = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["kind"] = kind,
                ["message"] = message,
                ["ts"] = now,
                ["diag"] = snapshot,
            });
            await PublishAsync(TOPIC_ALERT, payload, retain: false);
        }

        private async Task PublishAsync(string topic, string payload, bool retain)
        {
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithRetainFlag(retain)
                .Build();
            await _mqttClient.EnqueueAsync(message);
        }

        private async Task PublishDiagnosticsDiscoveryAsync()
        {
            var device = new Dictionary<string, object>
            {
                ["identifiers"] = new[] { "inferno_smoker" },
            };

            Dictionary<string, object> Diag(string name, string uniqueId, string template, Dictionary<string, object>? extra = null)
            {
                var d = new Dictionary<string, object>
                {
                    ["name"] = name,
                    ["state_topic"] = TOPIC_DIAG,
                    ["value_template"] = template,
                    ["entity_category"] = "diagnostic",
                    ["device"] = device,
                    ["unique_id"] = uniqueId,
                };
                foreach (var kv in extra ?? new()) d[kv.Key] = kv.Value;
                return d;
            }

            const string On = "ON", Off = "OFF";
            string Bool(string field, bool invert = false) =>
                $"{{{{ '{(invert ? Off : On)}' if value_json.{field} else '{(invert ? On : Off)}' }}}}";

            await PublishDiscoveryConfigAsync("sensor", "cpu_temp", Diag("CPU Temperature", "inferno_cpu_temp",
                "{{ value_json.cpu_temp_c }}",
                new() { ["unit_of_measurement"] = "°C", ["device_class"] = "temperature", ["state_class"] = "measurement" }), needsApi: false);

            await PublishDiscoveryConfigAsync("sensor", "wifi_signal", Diag("Wi-Fi Signal", "inferno_wifi_signal",
                "{{ value_json.wifi_dbm }}",
                new() { ["unit_of_measurement"] = "dBm", ["device_class"] = "signal_strength", ["state_class"] = "measurement" }), needsApi: false);

            await PublishDiscoveryConfigAsync("sensor", "load", Diag("CPU Load (1m)", "inferno_load",
                "{{ value_json.load_1m }}",
                new() { ["state_class"] = "measurement", ["icon"] = "mdi:cpu-64-bit" }), needsApi: false);

            await PublishDiscoveryConfigAsync("sensor", "mem_available", Diag("Memory Available", "inferno_mem_available",
                "{{ value_json.mem_available_mb }}",
                new() { ["unit_of_measurement"] = "MB", ["state_class"] = "measurement", ["icon"] = "mdi:memory" }), needsApi: false);

            await PublishDiscoveryConfigAsync("sensor", "disk_free", Diag("Disk Free", "inferno_disk_free",
                "{{ value_json.disk_free_pct }}",
                new() { ["unit_of_measurement"] = "%", ["state_class"] = "measurement", ["icon"] = "mdi:sd" }), needsApi: false);

            await PublishDiscoveryConfigAsync("sensor", "last_boot", Diag("Last Boot", "inferno_last_boot",
                "{{ value_json.boot_time }}",
                new() { ["device_class"] = "timestamp" }), needsApi: false);

            await PublishDiscoveryConfigAsync("sensor", "throttled_raw", Diag("Throttle Flags", "inferno_throttled_raw",
                "{{ value_json.throttled_raw }}",
                new() { ["icon"] = "mdi:flash-alert" }), needsApi: false);

            await PublishDiscoveryConfigAsync("binary_sensor", "undervoltage", Diag("Undervoltage", "inferno_undervoltage",
                Bool("undervoltage_now"), new() { ["device_class"] = "problem" }), needsApi: false);

            await PublishDiscoveryConfigAsync("binary_sensor", "undervoltage_since_boot", Diag("Undervoltage Since Boot", "inferno_undervoltage_since_boot",
                Bool("undervoltage_since_boot"), new() { ["device_class"] = "problem" }), needsApi: false);

            await PublishDiscoveryConfigAsync("binary_sensor", "throttled", Diag("CPU Throttled", "inferno_throttled",
                Bool("throttled_now"), new() { ["device_class"] = "problem" }), needsApi: false);

            await PublishDiscoveryConfigAsync("binary_sensor", "self_test", Diag("Self-Test", "inferno_self_test",
                Bool("self_test_ok", invert: true),
                new()
                {
                    ["device_class"] = "problem",
                    ["json_attributes_topic"] = TOPIC_DIAG,
                    ["json_attributes_template"] = "{{ {'failures': value_json.self_test_failures} | tojson }}",
                }), needsApi: false);

            await PublishDiscoveryConfigAsync("binary_sensor", "api", Diag("API", "inferno_api",
                Bool("api_ok"), new() { ["device_class"] = "connectivity" }), needsApi: false);
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
                    _stopCts.Cancel();
                    _stopCts.Dispose();
                    _mqttClient.Dispose();
                }
                disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}