namespace Lumen.Core;

public static class ColorMath
{
    /// <summary>RGB 0..255 → HSV (h 0..360, s 0..1, v 0..1).</summary>
    public static (double H, double S, double V) RgbToHsv(double r, double g, double b)
    {
        r /= 255; g /= 255; b /= 255;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;

        double h = 0;
        if (d > 1e-9)
        {
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
        }
        if (h < 0) h += 360;

        return (h, max <= 0 ? 0 : d / max, max);
    }

    /// <summary>HSV → RGB 0..255.</summary>
    public static (byte R, byte G, byte B) HsvToRgb(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);

        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;

        var (r, g, b) = (int)(h / 60) switch
        {
            0 => (c, x, 0d),
            1 => (x, c, 0d),
            2 => (0d, c, x),
            3 => (0d, x, c),
            4 => (x, 0d, c),
            _ => (c, 0d, x)
        };

        return ((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    /// <summary>
    /// Цветовая температура → RGB (приближение Таннера Хелланда по излучению чёрного тела),
    /// чтобы UI показывал основной свет того же оттенка, что и лампа: 2700 K — тёплый, 6500 K — белый.
    /// </summary>
    public static (byte R, byte G, byte B) KelvinToRgb(double kelvin)
    {
        var t = Math.Clamp(kelvin, 1000, 40000) / 100;
        double r, g, b;

        if (t <= 66)
        {
            r = 255;
            g = 99.4708025861 * Math.Log(t) - 161.1195681661;
            b = t <= 19 ? 0 : 138.5177312231 * Math.Log(t - 10) - 305.0447927307;
        }
        else
        {
            r = 329.698727446 * Math.Pow(t - 60, -0.1332047592);
            g = 288.1221695283 * Math.Pow(t - 60, -0.0755148492);
            b = 255;
        }

        static byte B(double v) => (byte)Math.Round(Math.Clamp(v, 0, 255));
        return (B(r), B(g), B(b));
    }

    /// <summary>Формат Yeelight: одно число R*65536 + G*256 + B (0 лампа не принимает).</summary>
    public static int ToYeelightRgb(byte r, byte g, byte b) => Math.Max(1, (r << 16) | (g << 8) | b);

    public static (byte R, byte G, byte B) FromYeelightRgb(int rgb)
        => ((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
}
