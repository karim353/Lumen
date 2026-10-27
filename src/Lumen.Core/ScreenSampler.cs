using System.Runtime.InteropServices;

namespace Lumen.Core;

/// <summary>
/// Быстрый захват «среднего» цвета экрана через GDI:
/// StretchBlt сжимает монитор до 64×36 (HALFTONE усредняет пиксели),
/// дальше считаем 2304 пикселя вместо 8 миллионов. Буферы создаются один раз.
/// </summary>
public sealed class ScreenSampler : IScreenCapture
{
    private const int W = 64, H = 36;

    private readonly IntPtr _memDc;
    private readonly IntPtr _bitmap;
    private readonly IntPtr _oldObj;
    private readonly IntPtr _bits;
    private bool _disposed;

    public ScreenSampler()
    {
        var screenDc = GetDC(IntPtr.Zero);
        try
        {
            _memDc = CreateCompatibleDC(screenDc);
            var bmi = new BITMAPINFO
            {
                biSize = Marshal.SizeOf<BITMAPINFO>(),
                biWidth = W,
                biHeight = -H, // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0 // BI_RGB
            };
            _bitmap = CreateDIBSection(screenDc, ref bmi, 0, out _bits, IntPtr.Zero, 0);
            if (_bitmap == IntPtr.Zero) throw new InvalidOperationException("CreateDIBSection failed");
            _oldObj = SelectObject(_memDc, _bitmap);
            SetStretchBltMode(_memDc, HALFTONE);
            SetBrushOrgEx(_memDc, 0, 0, IntPtr.Zero);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    public string Name => "GDI";

    private bool _hasFrame;

    public FrameImage? CopyLastFrame()
    {
        if (!_hasFrame || _disposed) return null;
        var copy = new byte[W * H * 4];
        Marshal.Copy(_bits, copy, 0, copy.Length);
        return new FrameImage(copy, W, H);
    }

    /// <summary>
    /// Захватывает монитор (координаты виртуального экрана, физические пиксели) и возвращает
    /// доминирующие цвета — всего кадра, левой и правой половины — и среднюю яркость.
    /// Насыщенные пиксели весят больше: серые интерфейсы не «замывают» картинку.
    /// Панель задач и чёрные полосы фильма в расчёт не идут.
    /// </summary>
    public unsafe ScreenSample Sample(DisplayMonitor monitor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var area = monitor.Bounds;
        var screenDc = GetDC(IntPtr.Zero);
        try
        {
            StretchBlt(_memDc, 0, 0, W, H, screenDc, area.Left, area.Top, area.Width, area.Height, SRCCOPY);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }

        _hasFrame = true;
        return Analyze(new ReadOnlySpan<byte>((void*)_bits, W * H * 4), W, H, RegionFor(monitor, W, H));
    }

    /// <summary>
    /// Рабочая область монитора (без панели задач) в пикселях уменьшенного кадра width×height.
    /// В полноэкранном видео панель скрыта, но полоска высотой ~3 % на цвет почти не влияет.
    /// </summary>
    internal static PixelRegion RegionFor(DisplayMonitor monitor, int width, int height)
    {
        var b = monitor.Bounds;
        var w = monitor.WorkArea;
        if (b.Width <= 0 || b.Height <= 0 || w.Width <= 0 || w.Height <= 0) return new PixelRegion(0, 0, width, height);

        int Col(int x) => (int)Math.Round((double)(x - b.Left) * width / b.Width);
        int Row(int y) => (int)Math.Round((double)(y - b.Top) * height / b.Height);

        var region = new PixelRegion(
            Math.Clamp(Col(w.Left), 0, width), Math.Clamp(Row(w.Top), 0, height),
            Math.Clamp(Col(w.Left + w.Width), 0, width), Math.Clamp(Row(w.Top + w.Height), 0, height));

        // Панель задач не может занимать полэкрана — значит, геометрия странная, берём всё
        return region.Width < width / 2 || region.Height < height / 2 ? new PixelRegion(0, 0, width, height) : region;
    }

    /// <summary>Разбор BGRA-кадра. Вынесен отдельно, чтобы его можно было проверить тестами.</summary>
    internal static ScreenSample Analyze(ReadOnlySpan<byte> bgra, int width, int height, PixelRegion? region = null)
    {
        var r0 = region ?? new PixelRegion(0, 0, width, height);
        var (x0, y0, x1, y1) = (r0.Left, r0.Top, r0.Right, r0.Bottom);
        TrimBlackBars(bgra, width, ref x0, ref y0, ref x1, ref y1);

        var all = new Accumulator();
        var left = new Accumulator();
        var right = new Accumulator();
        double lum = 0;
        var midX = (x0 + x1) / 2;

        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var i = (y * width + x) * 4;
                double b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
                var max = Math.Max(r, Math.Max(g, b));
                var min = Math.Min(r, Math.Min(g, b));
                var v = max / 255.0;
                var s = max <= 0 ? 0 : (max - min) / max;

                var w = 0.04 + s * s * v; // яркие и насыщенные пиксели доминируют
                all.Add(r, g, b, w);
                if (x < midX) left.Add(r, g, b, w);
                else right.Add(r, g, b, w);
                lum += v;
            }
        }

        var count = Math.Max(1, (x1 - x0) * (y1 - y0));
        return new ScreenSample(all.Result, left.Result, right.Result, lum / count, new PixelRegion(x0, y0, x1, y1));
    }

    /// <summary>
    /// Чёрные полосы фильма (2.39:1 на 16:9, 4:3 по бокам): срезаем с краёв строки и столбцы,
    /// где света почти нет. Не больше четверти кадра с каждой стороны — тёмная сцена целиком не «съедается».
    /// </summary>
    private static void TrimBlackBars(ReadOnlySpan<byte> bgra, int width, ref int x0, ref int y0, ref int x1, ref int y1)
    {
        const double BlackLevel = 14; // средний максимум каналов по линии, 0..255

        var maxRows = (y1 - y0) / 4;
        var maxCols = (x1 - x0) / 4;
        for (var n = 0; n < maxRows && LineMean(bgra, width, x0, x1, y0, y0 + 1) < BlackLevel; n++) y0++;
        for (var n = 0; n < maxRows && LineMean(bgra, width, x0, x1, y1 - 1, y1) < BlackLevel; n++) y1--;
        for (var n = 0; n < maxCols && LineMean(bgra, width, x0, x0 + 1, y0, y1) < BlackLevel; n++) x0++;
        for (var n = 0; n < maxCols && LineMean(bgra, width, x1 - 1, x1, y0, y1) < BlackLevel; n++) x1--;
    }

    private static double LineMean(ReadOnlySpan<byte> bgra, int width, int x0, int x1, int y0, int y1)
    {
        double sum = 0;
        var count = 0;
        for (var y = y0; y < y1; y++)
        for (var x = x0; x < x1; x++)
        {
            var i = (y * width + x) * 4;
            sum += Math.Max(bgra[i + 2], Math.Max(bgra[i + 1], bgra[i]));
            count++;
        }
        return count == 0 ? 255 : sum / count;
    }

    private struct Accumulator
    {
        private double _r, _g, _b, _w;

        public void Add(double r, double g, double b, double w)
        {
            _r += r * w; _g += g * w; _b += b * w; _w += w;
        }

        public readonly Rgb Result => _w > 0 ? new Rgb(_r / _w, _g / _w, _b / _w) : default;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SelectObject(_memDc, _oldObj);
        DeleteObject(_bitmap);
        DeleteDC(_memDc);
    }

    // ---- WinAPI ----
    private const int HALFTONE = 4;
    private const int SRCCOPY = 0x00CC0020; // без CAPTUREBLT — иначе мигает курсор

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        public int bmiColors;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] private static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr prev);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage,
        out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")]
    private static extern bool StretchBlt(IntPtr dst, int dx, int dy, int dw, int dh,
        IntPtr src, int sx, int sy, int sw, int sh, int rop);
}

public readonly record struct Rgb(double R, double G, double B);

/// <param name="Region">Какая часть уменьшенного кадра пошла в расчёт (после обрезки полос и панели задач).</param>
public readonly record struct ScreenSample(Rgb All, Rgb Left, Rgb Right, double Luminance, PixelRegion Region = default);

/// <summary>Прямоугольник в пикселях уменьшенного кадра; Right и Bottom — не включительно.</summary>
public readonly record struct PixelRegion(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

public readonly record struct ScreenRect(int Left, int Top, int Width, int Height);

/// <summary>
/// Монитор: имя устройства (\\.\DISPLAY1) стабильно между запусками — его и храним в настройках.
/// WorkArea — без панели задач.
/// </summary>
public sealed record DisplayMonitor(string DeviceName, ScreenRect Bounds, bool IsPrimary, ScreenRect WorkArea)
{
    public DisplayMonitor(string deviceName, ScreenRect bounds, bool isPrimary) : this(deviceName, bounds, isPrimary, bounds) { }
}

public static class DisplayMonitors
{
    /// <summary>Все мониторы в порядке слева направо (физические пиксели — приложение PerMonitorV2).</summary>
    public static IReadOnlyList<DisplayMonitor> GetAll()
    {
        var result = new List<DisplayMonitor>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMon, _, _, _) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMon, ref info))
            {
                var r = info.rcMonitor;
                var w = info.rcWork;
                result.Add(new DisplayMonitor(info.szDevice,
                    new ScreenRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top),
                    (info.dwFlags & MONITORINFOF_PRIMARY) != 0,
                    new ScreenRect(w.Left, w.Top, w.Right - w.Left, w.Bottom - w.Top)));
            }
            return true;
        }, IntPtr.Zero);

        return result.OrderBy(m => m.Bounds.Left).ThenBy(m => m.Bounds.Top).ToList();
    }

    /// <summary>Монитор по имени; если его отключили — основной.</summary>
    public static DisplayMonitor? Find(string? deviceName)
    {
        var all = GetAll();
        return all.FirstOrDefault(m => m.DeviceName == deviceName)
               ?? all.FirstOrDefault(m => m.IsPrimary)
               ?? all.FirstOrDefault();
    }

    private const uint MONITORINFOF_PRIMARY = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprcMonitor, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);
}
