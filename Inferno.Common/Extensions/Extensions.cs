using System;
using Inferno.Common.Models;

namespace Inferno.Common.Extensions
{
    public static class Extensions
    {
        public static double Clamp(this double toClamp, double minValue, double maxValue)
        {
            return double.Clamp(toClamp, minValue, maxValue);
        }

        public static int Clamp(this int toClamp, int minValue, int maxValue)
        {
            return int.Clamp(toClamp, minValue, maxValue);
        }

        public static bool IsCookingMode(this SmokerMode mode)
        {
            if (mode == SmokerMode.Smoke ||
                mode == SmokerMode.Hold ||
                mode == SmokerMode.Preheat)
            {
                return true;
            }
            else
            {
                return false;
            }
        }
    }
}