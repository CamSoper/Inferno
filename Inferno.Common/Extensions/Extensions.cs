using System;
using Inferno.Common.Models;

namespace Inferno.Common.Extensions
{
    public static class Extensions
    {
        public static double Clamp(this double toClamp, double minValue, double maxValue)
        {
            double returnValue = toClamp;
            returnValue = Math.Max(returnValue, minValue);
            returnValue = Math.Min(returnValue, maxValue);
            return returnValue;
        }

        public static int Clamp(this int toClamp, int minValue, int maxValue)
        {
            int returnValue = toClamp;
            returnValue = Math.Max(returnValue, minValue);
            returnValue = Math.Min(returnValue, maxValue);
            return returnValue;
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