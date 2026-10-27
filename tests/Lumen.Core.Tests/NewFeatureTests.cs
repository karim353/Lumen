using System.Runtime.CompilerServices;

namespace Lumen.Core.Tests;

internal static class TestSetup
{
    /// <summary>Журнал тестов — во временную папку, а не в настоящий %AppData%\Lumen.</summary>
    [ModuleInitializer]
    internal static void RedirectLog()
        => CommandLog.Folder = Path.Combine(Path.GetTempPath(), "Lumen.Tests", Environment.ProcessId.ToString());
}

public class CropTests
{
    private static byte[] Frame(int w, int h, Func<int, int, (byte R, byte G, byte B)> pixel)
    {
        var data = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var (r, g, b) = pixel(x, y);
            var i = (y * w + x) * 4;
            data[i] = b; data[i + 1] = g; data[i + 2] = r; data[i + 3] = 255;
        }
        return data;
    }

    [Fact]
    public void Letterbox_IsTrimmed_AndDoesNotDarkenBrightness()
    {
        // Фильм 2.39:1 на 16:9: по 4 чёрные строки сверху и снизу из 36
        var s = ScreenSampler.Analyze(Frame(64, 36, (_, y) => y < 4 || y >= 32 ? ((byte)0, (byte)0, (byte)0) : ((byte)255, (byte)140, (byte)0)), 64, 36);

        Assert.Equal(new PixelRegion(0, 4, 64, 32), s.Region);
        Assert.Equal(1, s.Luminance, 3); // без обрезки было бы 0.78
    }

    [Fact]
    public void Pillarbox_IsTrimmed_SoZonesSeeThePicture()
    {
        // 4:3 по центру 16:9: по 8 чёрных столбцов слева и справа; левая половина картинки красная, правая синяя
        var s = ScreenSampler.Analyze(Frame(64, 36, (x, _) =>
            x < 8 || x >= 56 ? ((byte)0, (byte)0, (byte)0) : x < 32 ? ((byte)255, (byte)0, (byte)0) : ((byte)0, (byte)0, (byte)255)), 64, 36);

        Assert.Equal(new PixelRegion(8, 0, 56, 36), s.Region);
        Assert.True(s.Left.R > 250 && s.Left.B < 5, $"left = {s.Left}");
        Assert.True(s.Right.B > 250 && s.Right.R < 5, $"right = {s.Right}");
    }

    [Fact]
    public void DarkScene_IsNotTrimmedAway()
    {
        var s = ScreenSampler.Analyze(new byte[64 * 36 * 4], 64, 36);
        Assert.True(s.Region.Width >= 32 && s.Region.Height >= 18, $"region = {s.Region}");
    }

    [Fact]
    public void SubtitlesInBar_KeepTheBar()
    {
        // В нижней полосе светлые субтитры — полоса уже не «чёрная», и её не режем
        var s = ScreenSampler.Analyze(Frame(64, 36, (x, y) =>
            y >= 32 ? (x is > 20 and < 44 ? ((byte)230, (byte)230, (byte)230) : ((byte)0, (byte)0, (byte)0)) : ((byte)40, (byte)90, (byte)200)), 64, 36);
        Assert.Equal(36, s.Region.Bottom);
    }

    [Fact]
    public void RegionFor_ExcludesTaskbar()
    {
        // 4K, панель задач 72 px снизу — как у пользователя
        var m = new DisplayMonitor(@"\\.\DISPLAY1", new ScreenRect(0, 0, 3840, 2160), true, new ScreenRect(0, 0, 3840, 2088));
        Assert.Equal(new PixelRegion(0, 0, 60, 32), ScreenSampler.RegionFor(m, 60, 33));

        // Панель слева, второй монитор с отрицательными координатами
        var left = new DisplayMonitor(@"\\.\DISPLAY2", new ScreenRect(-1920, 0, 1920, 1080), false, new ScreenRect(-1840, 0, 1840, 1080));
        Assert.Equal(new PixelRegion(3, 0, 64, 36), ScreenSampler.RegionFor(left, 64, 36));
    }
}

public class HdrTests
{
    private static byte[] Half(params (float R, float G, float B)[] pixels)
    {
        var data = new byte[pixels.Length * 8];
        for (var i = 0; i < pixels.Length; i++)
        {
            Write(data, i * 8, pixels[i].R);
            Write(data, i * 8 + 2, pixels[i].G);
            Write(data, i * 8 + 4, pixels[i].B);
            Write(data, i * 8 + 6, 1f);
        }
        return data;

        static void Write(byte[] d, int at, float v)
        {
            var bits = BitConverter.HalfToUInt16Bits((Half)v);
            d[at] = (byte)bits;
            d[at + 1] = (byte)(bits >> 8);
        }
    }

    [Fact]
    public void SdrWhite_BecomesFullWhite()
    {
        // Белый интерфейс при «Яркость SDR» = 200 нит приходит как 2.5 в scRGB
        var dst = new byte[4];
        DxgiScreenCapture.HdrToBgra8(Half((2.5f, 2.5f, 2.5f)), dst, sdrWhite: 2.5);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, dst);
    }

    [Fact]
    public void LinearMidGrey_IsSrgbEncoded_AndChannelsSwapped()
    {
        // 0.2140 линейных ≈ 128 в sRGB; на выходе порядок B,G,R,A
        var dst = new byte[4];
        DxgiScreenCapture.HdrToBgra8(Half((0.214f, 0f, 1f)), dst, sdrWhite: 1);
        Assert.Equal(255, dst[0]);              // B
        Assert.Equal(0, dst[1]);                // G
        Assert.InRange(dst[2], (byte)126, (byte)130); // R
    }

    [Fact]
    public void HighlightsClip_AndNegativesAreBlack()
    {
        var dst = new byte[8];
        DxgiScreenCapture.HdrToBgra8(Half((12f, 12f, 12f), (-0.5f, -0.1f, 0f)), dst, sdrWhite: 2.5);
        Assert.Equal(new byte[] { 255, 255, 255, 255, 0, 0, 0, 255 }, dst);
    }
}

public class CommandLogTests
{
    [Fact]
    public void Write_AppendsTimestampedLine()
    {
        var marker = "test-" + Guid.NewGuid().ToString("N");
        CommandLog.Write("UDP →", "bg_set_bright", [42, "smooth", 60]);
        CommandLog.Write(marker);
        CommandLog.Close();

        var lines = File.ReadAllLines(CommandLog.FilePath);
        Assert.Contains(lines, l => l.EndsWith("UDP → bg_set_bright [42,\"smooth\",60]"));
        var line = lines.Single(l => l.EndsWith(marker));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}  ", line);
    }

    [Fact]
    public void Format_QuotesStringsOnly()
    {
        Assert.Equal("[\"on\",\"smooth\",300]", CommandLog.Format(["on", "smooth", 300]));
        Assert.Equal("[]", CommandLog.Format([]));
    }
}

public class ThrottlerIntervalTests
{
    [Fact]
    public async Task Interval_CanBeShortenedOnTheFly()
    {
        var times = new List<long>();
        var throttler = new Throttler(TimeSpan.FromSeconds(5)) { Interval = TimeSpan.FromMilliseconds(20) };
        var sw = System.Diagnostics.Stopwatch.StartNew();

        for (var i = 0; i < 3; i++)
        {
            throttler.Run(() => { lock (times) times.Add(sw.ElapsedMilliseconds); return Task.CompletedTask; });
            await Task.Delay(60);
        }

        await Wait.UntilAsync(() => { lock (times) return times.Count == 3; });
        lock (times) Assert.True(times[^1] < 1000, $"последнее действие на {times[^1]} мс — интервал 5 с не сменился");
    }
}

public class KelvinTests
{
    [Fact]
    public void WarmIsOrange_DaylightIsNeutral_CoolIsBluish()
    {
        var (r1, g1, b1) = ColorMath.KelvinToRgb(2700);
        Assert.Equal(255, r1);
        Assert.True(g1 is > 150 and < 190 && b1 < 120, $"2700 K = {r1},{g1},{b1}");

        var (r2, g2, b2) = ColorMath.KelvinToRgb(6500);
        Assert.True(r2 > 245 && g2 > 245 && b2 > 245, $"6500 K = {r2},{g2},{b2}");

        var (r3, _, b3) = ColorMath.KelvinToRgb(10000);
        Assert.True(b3 > r3, "10000 K должен быть голубоватым");
    }
}

public class SchedulerTests
{
    private static ScheduleRule Rule(int h, int m, WeekDays days = WeekDays.All, ScheduleAction a = ScheduleAction.MainOn)
        => new() { Hour = h, Minute = m, Days = days, Action = a };

    [Fact]
    public void Due_FiresOnceInsideWindow()
    {
        var r = Rule(7, 30);
        var mon = new DateTime(2026, 10, 26, 7, 29, 50); // понедельник
        Assert.Single(Scheduler.Due([r], mon, mon.AddSeconds(15)));
        Assert.Empty(Scheduler.Due([r], mon.AddSeconds(15), mon.AddSeconds(30))); // повторно не срабатывает
    }

    [Fact]
    public void Due_RespectsDaysAndEnabled()
    {
        var sat = new DateTime(2026, 10, 31, 9, 59, 0); // суббота
        Assert.Empty(Scheduler.Due([Rule(10, 0, WeekDays.Weekdays)], sat, sat.AddMinutes(2)));
        Assert.Single(Scheduler.Due([Rule(10, 0, WeekDays.Weekend)], sat, sat.AddMinutes(2)));
        Assert.Empty(Scheduler.Due([new ScheduleRule { Hour = 10, Enabled = false }], sat, sat.AddMinutes(2)));
    }

    [Fact]
    public void Due_CrossesMidnight()
    {
        var r = Rule(0, 0);
        var late = new DateTime(2026, 10, 27, 23, 59, 30);
        Assert.Single(Scheduler.Due([r], late, late.AddMinutes(1)));
    }

    [Fact]
    public void Due_AfterSleep_DoesNotReplayWholeDay()
    {
        // ПК спал с 8:00 до 20:00: утренние 9:00 и 12:00 не выполняются, свежие 19:58 — выполняются
        var rules = new[] { Rule(9, 0), Rule(12, 0), Rule(19, 58, a: ScheduleAction.AllOff) };
        var day = new DateTime(2026, 10, 27);
        var due = Scheduler.Due(rules, day.AddHours(8), day.AddHours(20));
        Assert.Equal(ScheduleAction.AllOff, Assert.Single(due).Action);
    }

    [Fact]
    public void Upcoming_SortsAndSkipsPast()
    {
        var now = new DateTime(2026, 10, 27, 12, 0, 0); // вторник
        var up = Scheduler.Upcoming([Rule(8, 0), Rule(22, 30), Rule(18, 0, WeekDays.Sat)], now, 3);
        Assert.Equal(new DateTime(2026, 10, 27, 22, 30, 0), up[0].At);
        Assert.Equal(new DateTime(2026, 10, 28, 8, 0, 0), up[1].At);
        Assert.Equal(new DateTime(2026, 10, 31, 18, 0, 0), up[2].At);
    }
}
