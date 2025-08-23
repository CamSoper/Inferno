using System;

namespace Inferno.Api.Settings
{
    public class SmokerSettings
    {
        public int AugerPin { get; set; }
        public int BlowerPin { get; set; }
        public int IgniterPin { get; set; }
        public int MaxSetPoint { get; set; } = 400;
        public int MinSetPoint { get; set; } = 180;
        public int MaxGrillTemp { get; set; } = 425;
        public double ShutdownBlowerTimeoutMinutes { get; set; } = 10;
        public double HoldCycleSeconds { get; set; } = 10;
        public double UMax { get; set; } = 1.0;
        public double UMin { get; set; } = 0.175;
        public double PB { get; set; } = 60.0;
        public double Ti { get; set; } = 180.0;
        public double Td { get; set; } = 45.0;
    }
}
