using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Lumen.Core;

/// <summary>
/// Что ушло в лампу за кадр — для живого превью в UI. В режиме одной зоны Left == Right.
/// Fps — реальная частота цикла, Capture — «DXGI» или «GDI».
/// </summary>
public readonly record struct SyncFrame(int LeftRgb, int RightRgb, int Brightness, double Fps, string Capture);

/// <summary>
/// Цикл синхронизации: захват экрана → сглаживание → отправка в лампу через канал реального времени.
/// Цвет отправляется на полной яркости (так LED выглядят сочнее), интенсивность — через bg_set_bright.
/// </summary>
public sealed class ScreenSyncEngine : IAsyncDisposable
{
    /// <summary>
    /// После set_segment_rgb lamp15 примерно секунду игнорирует bg_set_bright (замерено: 0.9 с — нет, 1.1 с — да).
    /// </summary>
    private static readonly TimeSpan SegmentBrightnessLock = TimeSpan.FromMilliseconds(1150);

    private readonly IRealtimeChannel _channel;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public ScreenSyncEngine(IRealtimeChannel channel) => _channel = channel;

    /// <summary>Кадров в секунду (5..30).</summary>
    public int Fps { get; set; } = 20;

    /// <summary>0 — мгновенно, 0.9 — очень плавно.</summary>
    public double Smoothing { get; set; } = 0.5;

    /// <summary>Усиление насыщенности (1 — как на экране, 2 — вдвое ярче цвета).</summary>
    public double SaturationBoost { get; set; } = 1.3;

    /// <summary>Яркость подсветки повторяет яркость кадра (только в режиме одной зоны).</summary>
    public bool FollowBrightness { get; set; } = true;

    /// <summary>Яркость 1..100: потолок при FollowBrightness, иначе — постоянная.</summary>
    public int Brightness { get; set; } = 100;

    /// <summary>Левая и правая половины подсветки повторяют свои половины экрана (если лампа умеет сегменты).</summary>
    public bool Zones { get; set; }

    /// <summary>Лампа считает левой другую половину — меняем зоны местами.</summary>
    public bool SwapZones { get; set; }

    /// <summary>Какой монитор захватывать (\\.\DISPLAY1); null — основной.</summary>
    public string? MonitorDeviceName { get; set; }

    public bool IsRunning => _loop is { IsCompleted: false };

    public event Action<Exception>? Faulted;
    public event Action<SyncFrame>? FrameSent;

    /// <summary>Миниатюра экрана ~10 раз в секунду — её показывает монитор в 3D-сцене.</summary>
    public event Action<FrameImage>? Thumbnail;

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Factory.StartNew(() => RunAsync(token), token,
            TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    public async Task StopAsync()
    {
        if (_cts == null) return;
        _cts.Cancel();
        try { if (_loop != null) await _loop.ConfigureAwait(false); } catch { /* ignore */ }
        _cts.Dispose();
        _cts = null;
        _loop = null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        // Таймер Windows по умолчанию тикает раз в 15,6 мс: при 20 к/с Task.Delay(45) спит 47–62 мс,
        // и вместо 20 кадров выходит ~16. На время синхронизации просим шаг 1 мс.
        var precise = TryBeginPreciseTimer();
        try
        {
            using var capture = ScreenCapture.Create();
            await _channel.SendAsync("bg_set_power", "on", "smooth", 300).ConfigureAwait(false);

            var all = new ColorFilter();
            var left = new ColorFilter();
            var right = new ColorFilter();
            double lum = -1;

            var lastRgb = -1;
            int lastLeft = -1, lastRight = -1;
            var lastBright = -1;
            var sinceSegment = new Stopwatch(); // не запущен — сегментов ещё не было

            string? monitorName = null;
            DisplayMonitor? monitor = null;
            var frame = 0;
            var clock = Stopwatch.StartNew();
            double nextFrameAt = 0; // расписание кадров: паузы считаем от него, а не от начала кадра
            var fpsClock = Stopwatch.StartNew();
            var thumbClock = Stopwatch.StartNew();
            double fps = 0;

            while (!ct.IsCancellationRequested)
            {
                var frameMs = 1000 / Math.Clamp(Fps, 1, 60);

                // Раз в ~2 секунды перечитываем геометрию: монитор могли отключить или сменить разрешение
                if (monitor == null || frame++ % 60 == 0 || monitorName != MonitorDeviceName)
                {
                    monitorName = MonitorDeviceName;
                    monitor = DisplayMonitors.Find(monitorName);
                }

                if (monitor != null)
                {
                    var sample = capture.Sample(monitor);
                    if (Thumbnail != null && thumbClock.ElapsedMilliseconds >= 100 && capture.CopyLastFrame() is { } thumb)
                    {
                        thumbClock.Restart();
                        Thumbnail.Invoke(thumb);
                    }
                    var smoothing = Math.Clamp(Smoothing, 0, 0.95);
                    var transition = Math.Max(50, frameMs);
                    lum = lum < 0 ? sample.Luminance : lum + (sample.Luminance - lum) * (1 - smoothing);

                    var brightLocked = sinceSegment.IsRunning && sinceSegment.Elapsed < SegmentBrightnessLock;
                    var maxBright = Math.Clamp(Brightness, 1, 100);

                    if (Zones && _channel.SupportsSegments)
                    {
                        var l = left.Update(sample.Left, SaturationBoost, smoothing);
                        var r = right.Update(sample.Right, SaturationBoost, smoothing);

                        if (maxBright != lastBright)
                        {
                            // Яркость меняется только в паузе между сегментами: держим кадры, пока лампа «не отпустит»
                            if (!brightLocked)
                            {
                                await _channel.SendAsync("bg_set_bright", maxBright, "smooth", 200).ConfigureAwait(false);
                                lastBright = maxBright;
                            }
                        }
                        else if (lastLeft < 0 || ColorDistance(l, lastLeft) > 6 || ColorDistance(r, lastRight) > 6)
                        {
                            // Яркость цвета lamp15 в сегментах игнорирует, важны только тон и насыщенность
                            if (SwapZones) await _channel.SendAsync("set_segment_rgb", r, l).ConfigureAwait(false);
                            else await _channel.SendAsync("set_segment_rgb", l, r).ConfigureAwait(false);
                            lastLeft = l;
                            lastRight = r;
                            lastRgb = -1;
                            sinceSegment.Restart();
                        }

                        FrameSent?.Invoke(new SyncFrame(l, r, lastBright, fps, capture.Name));
                    }
                    else
                    {
                        var rgb = all.Update(sample.All, SaturationBoost, smoothing);
                        if (lastRgb < 0 || ColorDistance(rgb, lastRgb) > 6)
                        {
                            // bg_set_rgb заодно выводит лампу из режима сегментов
                            await _channel.SendAsync("bg_set_rgb", rgb, "smooth", transition).ConfigureAwait(false);
                            lastRgb = rgb;
                            lastLeft = -1;
                        }

                        var bright = FollowBrightness ? FollowedBrightness(lum, maxBright) : maxBright;
                        var threshold = FollowBrightness ? 3 : 1;
                        if (!brightLocked && (lastBright < 0 || Math.Abs(bright - lastBright) >= threshold))
                        {
                            await _channel.SendAsync("bg_set_bright", bright, "smooth", transition).ConfigureAwait(false);
                            lastBright = bright;
                        }

                        FrameSent?.Invoke(new SyncFrame(rgb, rgb, lastBright, fps, capture.Name));
                    }
                }

                nextFrameAt += frameMs;
                var wait = nextFrameAt - clock.Elapsed.TotalMilliseconds;
                if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(wait), ct).ConfigureAwait(false);
                else if (wait < -frameMs) nextFrameAt = clock.Elapsed.TotalMilliseconds; // отстали — не догоняем пачкой кадров

                // Сглаженная реальная частота — видно, если захват не успевает
                var dt = fpsClock.Elapsed.TotalSeconds;
                fpsClock.Restart();
                if (dt > 0) fps = fps <= 0 ? 1 / dt : fps + (1 / dt - fps) * 0.1;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Faulted?.Invoke(ex);
        }
        finally
        {
            if (precise) timeEndPeriod(1);
        }
    }

    private static bool TryBeginPreciseTimer()
    {
        try { return timeBeginPeriod(1) == 0; }
        catch { return false; } // не Windows или нет winmm — живём с обычным таймером
    }

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint period);

    /// <summary>Тёмная сцена — 5 % от потолка, яркая — весь потолок; гамма 0.7 поднимает полутона.</summary>
    internal static int FollowedBrightness(double luminance, int maxBright)
    {
        var k = 0.05 + 0.95 * Math.Pow(Math.Clamp(luminance, 0, 1), 0.7);
        return (int)Math.Round(Math.Clamp(maxBright * k, 1, 100));
    }

    /// <summary>Сглаживание в HSV: тон идёт по кратчайшей дуге, иначе красный↔пурпурный «пробегает» радугу.</summary>
    private sealed class ColorFilter
    {
        private double _h, _s;
        private bool _first = true;

        public int Update(Rgb c, double saturationBoost, double smoothing)
        {
            var (th, ts, _) = ColorMath.RgbToHsv(c.R, c.G, c.B);
            ts = Math.Clamp(ts * saturationBoost, 0, 1);

            if (_first)
            {
                _h = th; _s = ts; _first = false;
            }
            else
            {
                var k = 1 - smoothing;
                _h = LerpHue(_h, th, k);
                _s += (ts - _s) * k;
            }

            var (r, g, b) = ColorMath.HsvToRgb(_h, _s, 1);
            return ColorMath.ToYeelightRgb(r, g, b);
        }
    }

    internal static double LerpHue(double from, double to, double k)
    {
        var diff = ((to - from + 540) % 360) - 180; // кратчайший путь по кругу
        return (from + diff * k + 360) % 360;
    }

    internal static int ColorDistance(int a, int b)
    {
        var (r1, g1, b1) = ColorMath.FromYeelightRgb(a);
        var (r2, g2, b2) = ColorMath.FromYeelightRgb(b);
        return Math.Abs(r1 - r2) + Math.Abs(g1 - g2) + Math.Abs(b1 - b2);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        Faulted = null;
        FrameSent = null;
        Thumbnail = null;
    }
}
