using System;
using System.Windows.Media;

namespace ColorRamp
{
    // ProcessorとViewModelで共有するヘルパー
    internal static class HsvHslHelper
    {
        public enum ColorSpace { HSV, HSL }

        public struct ColorComponents
        {
            public float H, S, V, A; // V holds L for HSL
        }

        public static ColorComponents FromColor(Color c, ColorSpace mode)
        {
            float r = c.ScR;
            float g = c.ScG;
            float b = c.ScB;
            float max = Math.Max(r, Math.Max(g, b));
            float min = Math.Min(r, Math.Min(g, b));
            float delta = max - min;

            float h = 0;
            if (delta > 0)
            {
                if (max == r) h = (g - b) / delta + (g < b ? 6 : 0);
                else if (max == g) h = (b - r) / delta + 2;
                else h = (r - g) / delta + 4;
                h *= 60;
            }

            float s = 0;
            float v = 0;

            if (mode == ColorSpace.HSV)
            {
                v = max;
                s = max == 0 ? 0 : delta / max;
            }
            else // HSL
            {
                v = (max + min) / 2;
                s = (delta == 0) ? 0 : delta / (1 - Math.Abs(2 * v - 1));
            }

            return new ColorComponents { H = h, S = s, V = v, A = c.ScA };
        }

        public static Color ToColor(float h, float s, float v, float a, ColorSpace mode)
        {
            float r, g, b;

            if (mode == ColorSpace.HSV)
            {
                if (s == 0) { r = g = b = v; }
                else
                {
                    h /= 60;
                    int i = (int)Math.Floor(h);
                    float f = h - i;
                    float p = v * (1 - s);
                    float q = v * (1 - s * f);
                    float t = v * (1 - s * (1 - f));
                    switch (i % 6)
                    {
                        case 0: r = v; g = t; b = p; break;
                        case 1: r = q; g = v; b = p; break;
                        case 2: r = p; g = v; b = t; break;
                        case 3: r = p; g = q; b = v; break;
                        case 4: r = t; g = p; b = v; break;
                        default: r = v; g = p; b = q; break;
                    }
                }
            }
            else
            {
                float l = v;
                if (s == 0) { r = g = b = l; }
                else
                {
                    float q = l < 0.5 ? l * (1 + s) : l + s - l * s;
                    float p = 2 * l - q;
                    r = HueToRgb(p, q, h / 360 + 1.0f / 3);
                    g = HueToRgb(p, q, h / 360);
                    b = HueToRgb(p, q, h / 360 - 1.0f / 3);
                }
            }
            return Color.FromScRgb(a, r, g, b);
        }

        private static float HueToRgb(float p, float q, float t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0f / 6) return p + (q - p) * 6 * t;
            if (t < 1.0f / 2) return q;
            if (t < 2.0f / 3) return p + (q - p) * (2.0f / 3 - t) * 6;
            return p;
        }
    }
}