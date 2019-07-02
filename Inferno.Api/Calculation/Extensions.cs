using System;

namespace Inferno.Api.Calculation
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
    }
}