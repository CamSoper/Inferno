using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Serialization;

namespace Inferno.Mqtt.Services
{
    /// <summary>
    /// Firmware throttle/undervoltage flags, decoded from `vcgencmd get_throttled`.
    /// </summary>
    public sealed record ThrottleState(int Raw)
    {
        public bool UndervoltageNow => (Raw & 0x1) != 0;
        public bool FrequencyCappedNow => (Raw & 0x2) != 0;
        public bool ThrottledNow => (Raw & 0x4) != 0;
        public bool SoftTempLimitNow => (Raw & 0x8) != 0;
        public bool UndervoltageSinceBoot => (Raw & 0x10000) != 0;
        public bool FrequencyCappedSinceBoot => (Raw & 0x20000) != 0;
        public bool ThrottledSinceBoot => (Raw & 0x40000) != 0;
        public bool SoftTempLimitSinceBoot => (Raw & 0x80000) != 0;
    }

    /// <summary>
    /// One snapshot of the Pi's health. Serialized as the retained inferno/diag/state payload.
    /// </summary>
    public sealed class HealthSnapshot
    {
        [JsonPropertyName("ts")] public DateTimeOffset Timestamp { get; init; }
        [JsonPropertyName("boot_time")] public DateTimeOffset? BootTime { get; init; }
        [JsonPropertyName("uptime_s")] public long? UptimeSeconds { get; init; }
        [JsonPropertyName("throttled_raw")] public string? ThrottledRaw { get; init; }
        [JsonPropertyName("undervoltage_now")] public bool? UndervoltageNow { get; init; }
        [JsonPropertyName("undervoltage_since_boot")] public bool? UndervoltageSinceBoot { get; init; }
        [JsonPropertyName("throttled_now")] public bool? ThrottledNow { get; init; }
        [JsonPropertyName("throttled_since_boot")] public bool? ThrottledSinceBoot { get; init; }
        [JsonPropertyName("cpu_temp_c")] public double? CpuTempC { get; init; }
        [JsonPropertyName("load_1m")] public double? Load1m { get; init; }
        [JsonPropertyName("mem_available_mb")] public long? MemAvailableMb { get; init; }
        [JsonPropertyName("disk_free_pct")] public double? DiskFreePct { get; init; }
        [JsonPropertyName("wifi_dbm")] public int? WifiDbm { get; init; }
        [JsonPropertyName("spi_present")] public bool SpiPresent { get; init; }
        [JsonPropertyName("i2c_present")] public bool I2cPresent { get; init; }
        [JsonPropertyName("boot_config_ok")] public bool BootConfigOk { get; init; }
        [JsonPropertyName("fsck_recovered_files")] public int FsckRecoveredFiles { get; init; }
        [JsonPropertyName("api_ok")] public bool ApiOk { get; init; }
        [JsonPropertyName("self_test_failures")] public List<string> SelfTestFailures { get; init; } = new();
        [JsonPropertyName("self_test_ok")] public bool SelfTestOk => SelfTestFailures.Count == 0;
    }

    /// <summary>
    /// Reads Pi health from the kernel, firmware and filesystem. Every probe is
    /// best-effort: a probe that fails yields null rather than throwing, so a
    /// sick Pi still reports whatever it can.
    /// </summary>
    public sealed class HealthMonitor
    {
        public const string BootDir = "/boot/firmware";

        private readonly Func<bool> _apiOk;

        public HealthMonitor(Func<bool> apiOk)
        {
            _apiOk = apiOk;
        }

        public ThrottleState? ReadThrottle()
        {
            var output = RunCommand("vcgencmd", "get_throttled");
            return output is null ? null : ParseThrottled(output);
        }

        public HealthSnapshot Collect(ThrottleState? throttle = null)
        {
            throttle ??= ReadThrottle();
            var uptime = ReadUptimeSeconds();
            var now = DateTimeOffset.Now;

            bool spi = File.Exists("/dev/spidev0.0");
            bool i2c = File.Exists("/dev/i2c-1");
            bool bootConfigOk = CheckBootConfig(Path.Combine(BootDir, "config.txt"));
            int fsckFiles = CountFsckRecoveredFiles(BootDir);

            var failures = new List<string>();
            if (!spi) failures.Add("SPI device /dev/spidev0.0 missing (ADC unreachable)");
            if (!i2c) failures.Add("I2C device /dev/i2c-1 missing (LCD unreachable)");
            if (!bootConfigOk) failures.Add("config.txt is empty or missing dtparam=spi=on");
            if (fsckFiles > 0) failures.Add($"{fsckFiles} FSCK*.REC file(s) on boot partition (FAT was repaired after a bad shutdown)");

            return new HealthSnapshot
            {
                Timestamp = now,
                UptimeSeconds = uptime,
                BootTime = uptime is null ? null : now.AddSeconds(-uptime.Value),
                ThrottledRaw = throttle is null ? null : $"0x{throttle.Raw:x}",
                UndervoltageNow = throttle?.UndervoltageNow,
                UndervoltageSinceBoot = throttle?.UndervoltageSinceBoot,
                ThrottledNow = throttle?.ThrottledNow,
                ThrottledSinceBoot = throttle?.ThrottledSinceBoot,
                CpuTempC = ReadCpuTempC(),
                Load1m = ReadLoad1m(),
                MemAvailableMb = ReadMemAvailableMb(),
                DiskFreePct = ReadDiskFreePct(),
                WifiDbm = ReadWifiDbm(),
                SpiPresent = spi,
                I2cPresent = i2c,
                BootConfigOk = bootConfigOk,
                FsckRecoveredFiles = fsckFiles,
                ApiOk = _apiOk(),
                SelfTestFailures = failures,
            };
        }

        // ---- Parsers (internal for tests) ----

        /// <summary>Parses "throttled=0x50005".</summary>
        internal static ThrottleState? ParseThrottled(string output)
        {
            var idx = output.IndexOf("0x", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            var hex = new string(output[(idx + 2)..].TakeWhile(Uri.IsHexDigit).ToArray());
            return int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var raw)
                ? new ThrottleState(raw)
                : null;
        }

        /// <summary>Parses the wlan0 signal level (dBm) from /proc/net/wireless.</summary>
        internal static int? ParseWifiDbm(string procNetWireless, string iface = "wlan0")
        {
            foreach (var line in procNetWireless.Split('\n'))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith(iface + ":")) continue;
                // wlan0: 0000   70.  -40.  -256  ...
                var parts = trimmed[(iface.Length + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) return null;
                return double.TryParse(parts[2].TrimEnd('.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var dbm)
                    ? (int)dbm
                    : null;
            }
            return null;
        }

        internal static bool CheckBootConfig(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                var text = File.ReadAllText(path);
                return text.Split('\n').Any(l => l.Trim() == "dtparam=spi=on");
            }
            catch
            {
                return false;
            }
        }

        internal static int CountFsckRecoveredFiles(string dir)
        {
            try
            {
                return Directory.Exists(dir)
                    ? Directory.GetFiles(dir, "FSCK*.REC").Length
                    : 0;
            }
            catch
            {
                return 0;
            }
        }

        // ---- Probes ----

        private static long? ReadUptimeSeconds()
        {
            var text = TryRead("/proc/uptime");
            if (text is null) return null;
            var first = text.Split(' ')[0];
            return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? (long)s : null;
        }

        private static double? ReadCpuTempC()
        {
            var text = TryRead("/sys/class/thermal/thermal_zone0/temp");
            return int.TryParse(text?.Trim(), out var milli) ? Math.Round(milli / 1000.0, 1) : null;
        }

        private static double? ReadLoad1m()
        {
            var text = TryRead("/proc/loadavg");
            if (text is null) return null;
            return double.TryParse(text.Split(' ')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var l) ? l : null;
        }

        private static long? ReadMemAvailableMb()
        {
            var text = TryRead("/proc/meminfo");
            var line = text?.Split('\n').FirstOrDefault(l => l.StartsWith("MemAvailable:"));
            if (line is null) return null;
            var kb = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1];
            return long.TryParse(kb, out var k) ? k / 1024 : null;
        }

        private static double? ReadDiskFreePct()
        {
            try
            {
                var d = new DriveInfo("/");
                return Math.Round(100.0 * d.AvailableFreeSpace / d.TotalSize, 1);
            }
            catch
            {
                return null;
            }
        }

        private static int? ReadWifiDbm()
        {
            var text = TryRead("/proc/net/wireless");
            return text is null ? null : ParseWifiDbm(text);
        }

        private static string? TryRead(string path)
        {
            try { return File.ReadAllText(path); }
            catch { return null; }
        }

        private static string? RunCommand(string file, string args)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(file, args)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });
                if (p is null) return null;
                var output = p.StandardOutput.ReadToEnd();
                return p.WaitForExit(5000) && p.ExitCode == 0 ? output : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
