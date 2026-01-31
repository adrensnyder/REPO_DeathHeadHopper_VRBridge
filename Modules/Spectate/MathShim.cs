#nullable enable

namespace DeathHeadHopperVRBridge.Modules.Spectate
{
    internal static class Math
    {
        internal static int Max(int x, int y) => global::System.Math.Max(x, y);

        internal static int Clamp(int value, int min, int max)
        {
            if (value < min)
            {
                return min;
            }
            if (value > max)
            {
                return max;
            }
            return value;
        }
    }
}
