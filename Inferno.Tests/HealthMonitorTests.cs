using Inferno.Mqtt.Services;

namespace Inferno.Tests
{
    public class HealthMonitorTests
    {
        [Fact]
        public void ParseThrottled_DecodesUndervoltageNowAndSinceBoot()
        {
            // The value inferno reported on 2026-11-15 with a sagging PSU.
            var t = HealthMonitor.ParseThrottled("throttled=0x50005\n");

            Assert.NotNull(t);
            Assert.True(t!.UndervoltageNow);
            Assert.True(t.ThrottledNow);
            Assert.False(t.FrequencyCappedNow);
            Assert.True(t.UndervoltageSinceBoot);
            Assert.True(t.ThrottledSinceBoot);
            Assert.False(t.SoftTempLimitSinceBoot);
        }

        [Fact]
        public void ParseThrottled_HistoryOnly()
        {
            var t = HealthMonitor.ParseThrottled("throttled=0x50000");

            Assert.False(t!.UndervoltageNow);
            Assert.True(t.UndervoltageSinceBoot);
        }

        [Theory]
        [InlineData("throttled=0x0", 0)]
        [InlineData("throttled=0x80008", 0x80008)]
        public void ParseThrottled_Raw(string input, int expected)
        {
            Assert.Equal(expected, HealthMonitor.ParseThrottled(input)!.Raw);
        }

        [Theory]
        [InlineData("")]
        [InlineData("VCHI initialization failed")]
        public void ParseThrottled_GarbageIsNull(string input)
        {
            Assert.Null(HealthMonitor.ParseThrottled(input));
        }

        [Fact]
        public void ParseWifiDbm_ReadsSignalLevel()
        {
            const string proc =
                "Inter-| sta-|   Quality        |   Discarded packets               | Missed | WE\n" +
                " face | tus | link level noise |  nwid  crypt   frag  retry   misc | beacon | 22\n" +
                " wlan0: 0000   70.  -40.  -256        0      0      0      2      0        0\n";

            Assert.Equal(-40, HealthMonitor.ParseWifiDbm(proc));
        }

        [Fact]
        public void ParseWifiDbm_NoInterfaceIsNull()
        {
            Assert.Null(HealthMonitor.ParseWifiDbm("Inter-| sta-|\n face | tus |\n"));
        }

        [Fact]
        public void CheckBootConfig_EmptyFileFails()
        {
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, "");
                Assert.False(HealthMonitor.CheckBootConfig(path));

                File.WriteAllText(path, "#dtparam=spi=on\ndtparam=audio=on\n");
                Assert.False(HealthMonitor.CheckBootConfig(path));

                File.WriteAllText(path, "dtparam=i2c_arm=on\ndtparam=spi=on\n");
                Assert.True(HealthMonitor.CheckBootConfig(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void CountFsckRecoveredFiles_FindsRecFiles()
        {
            var dir = Directory.CreateTempSubdirectory().FullName;
            try
            {
                Assert.Equal(0, HealthMonitor.CountFsckRecoveredFiles(dir));
                File.WriteAllText(Path.Combine(dir, "FSCK0000.REC"), "x");
                File.WriteAllText(Path.Combine(dir, "FSCK0001.REC"), "x");
                Assert.Equal(2, HealthMonitor.CountFsckRecoveredFiles(dir));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void CountFsckRecoveredFiles_MissingDirIsZero()
        {
            Assert.Equal(0, HealthMonitor.CountFsckRecoveredFiles("/nonexistent/boot"));
        }
    }
}
