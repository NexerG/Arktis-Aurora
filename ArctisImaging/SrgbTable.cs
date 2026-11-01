using System;

namespace Arctis.Imaging
{
    // sRGB transfer both ways: byte to linear by table, linear to byte by searching the rounding thresholds.
    internal static class SrgbTable
    {
        internal static readonly float[] toLinear = BuildToLinear();
        // thresholds[i] is the linear value of encoded (i + 0.5) / 255; a value at or above it rounds past i
        private static readonly float[] thresholds = BuildThresholds();

        internal static byte FromLinear(float v)
        {
            float[] t = thresholds;
            int lo = 0, hi = t.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (v >= t[mid]) lo = mid + 1;
                else hi = mid;
            }
            return (byte)lo;
        }

        private static float Expand(double c) => (float)(c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4));

        private static float[] BuildToLinear()
        {
            float[] t = new float[256];
            for (int i = 0; i < 256; i++) t[i] = Expand(i / 255.0);
            return t;
        }

        private static float[] BuildThresholds()
        {
            float[] t = new float[255];
            for (int i = 0; i < 255; i++) t[i] = Expand((i + 0.5) / 255.0);
            return t;
        }
    }
}
