namespace Lumen.Core.Tests;

public class ThrottlerTests
{
    [Fact]
    public async Task OnlyLatestValue_IsSentAfterBurst()
    {
        var sent = new List<int>();
        var throttler = new Throttler(TimeSpan.FromMilliseconds(100));

        for (var i = 1; i <= 20; i++)
        {
            var v = i;
            throttler.Run(() => { lock (sent) sent.Add(v); return Task.CompletedTask; });
        }

        await Wait.UntilAsync(() => { lock (sent) return sent.Contains(20); });
        await Task.Delay(250);
        lock (sent)
        {
            Assert.Equal(20, sent[^1]);
            Assert.True(sent.Count <= 3, $"отправлено {sent.Count}, ожидалось не больше 3");
        }
    }

    [Fact]
    public async Task QuotaError_RetriesLastValueUntilAccepted()
    {
        var attempts = 0;
        var states = new List<bool>();
        var throttler = new Throttler(TimeSpan.FromMilliseconds(10)) { QuotaBackoff = TimeSpan.FromMilliseconds(30) };
        throttler.QuotaStateChanged += s => { lock (states) states.Add(s); };

        throttler.Run(() =>
        {
            // Лампа дважды отказывает по лимиту, на третий раз принимает
            if (Interlocked.Increment(ref attempts) <= 2) throw new YeelightQuotaException("client quota exceeded");
            return Task.CompletedTask;
        });

        await Wait.UntilAsync(() => Volatile.Read(ref attempts) == 3);
        await Wait.UntilAsync(() => { lock (states) return states.Count == 2; });
        lock (states) Assert.Equal([true, false], states);
    }

    [Fact]
    public async Task QuotaError_NewerValueReplacesRetry()
    {
        var sent = new List<int>();
        var throttler = new Throttler(TimeSpan.FromMilliseconds(10)) { QuotaBackoff = TimeSpan.FromMilliseconds(80) };
        var firstFailed = new TaskCompletionSource();

        throttler.Run(() =>
        {
            firstFailed.TrySetResult();
            throw new YeelightQuotaException("client quota exceeded");
        });
        await firstFailed.Task;
        throttler.Run(() => { lock (sent) sent.Add(2); return Task.CompletedTask; });

        await Wait.UntilAsync(() => { lock (sent) return sent.Count == 1; });
        await Task.Delay(200);
        lock (sent) Assert.Equal([2], sent); // старое значение не «догнало» новое
    }
}

public class ColorMathTests
{
    [Theory]
    [InlineData(255, 0, 0, 0, 1, 1)]
    [InlineData(0, 255, 0, 120, 1, 1)]
    [InlineData(0, 0, 255, 240, 1, 1)]
    [InlineData(255, 255, 255, 0, 0, 1)]
    [InlineData(0, 0, 0, 0, 0, 0)]
    public void RgbToHsv_KnownColors(double r, double g, double b, double h, double s, double v)
    {
        var (hh, ss, vv) = ColorMath.RgbToHsv(r, g, b);
        Assert.Equal(h, hh, 3);
        Assert.Equal(s, ss, 3);
        Assert.Equal(v, vv, 3);
    }

    [Theory]
    [InlineData(255, 106, 0)]
    [InlineData(18, 200, 77)]
    [InlineData(138, 0, 255)]
    [InlineData(34, 34, 34)]
    public void HsvRoundTrip_KeepsColor(byte r, byte g, byte b)
    {
        var (h, s, v) = ColorMath.RgbToHsv(r, g, b);
        var (r2, g2, b2) = ColorMath.HsvToRgb(h, s, v);
        Assert.InRange(Math.Abs(r - r2) + Math.Abs(g - g2) + Math.Abs(b - b2), 0, 2);
    }

    [Fact]
    public void YeelightRgb_PacksAndNeverZero()
    {
        Assert.Equal(0xFF8000, ColorMath.ToYeelightRgb(255, 128, 0));
        Assert.Equal(1, ColorMath.ToYeelightRgb(0, 0, 0)); // 0 лампа не принимает
        Assert.Equal(((byte)255, (byte)128, (byte)0), ColorMath.FromYeelightRgb(0xFF8000));
    }
}

public class ScreenAnalysisTests
{
    private static byte[] Frame(int w, int h, Func<int, int, (byte R, byte G, byte B)> pixel)
    {
        var data = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var (r, g, b) = pixel(x, y);
            var i = (y * w + x) * 4;
            data[i] = b; data[i + 1] = g; data[i + 2] = r; data[i + 3] = 255; // BGRA
        }
        return data;
    }

    [Fact]
    public void LeftAndRightHalves_AreSeparate()
    {
        var s = ScreenSampler.Analyze(Frame(32, 18, (x, _) => x < 16 ? ((byte)255, (byte)0, (byte)0) : ((byte)0, (byte)0, (byte)255)), 32, 18);

        Assert.True(s.Left.R > 250 && s.Left.B < 5, $"left = {s.Left}");
        Assert.True(s.Right.B > 250 && s.Right.R < 5, $"right = {s.Right}");
        Assert.Equal(1, s.Luminance, 3);
    }

    [Fact]
    public void SaturatedPixels_DominateGreyInterface()
    {
        // 80 % кадра — серый интерфейс, 20 % — ярко-зелёная картинка
        var s = ScreenSampler.Analyze(Frame(32, 18, (x, _) => x < 26 ? ((byte)60, (byte)60, (byte)60) : ((byte)0, (byte)230, (byte)60)), 32, 18);

        var (h, sat, _) = ColorMath.RgbToHsv(s.All.R, s.All.G, s.All.B);
        Assert.InRange(h, 100, 160);
        Assert.True(sat > 0.6, $"насыщенность {sat:0.00}");
    }

    [Fact]
    public void BlackFrame_HasZeroLuminance()
    {
        var s = ScreenSampler.Analyze(new byte[32 * 18 * 4], 32, 18);
        Assert.Equal(0, s.Luminance);
    }
}

public class SyncMathTests
{
    [Theory]
    [InlineData(350, 10, 0.5, 0)]   // через 0°, а не через всю радугу
    [InlineData(10, 350, 0.5, 0)]
    [InlineData(100, 200, 0.5, 150)]
    [InlineData(0, 180, 1, 180)]
    public void LerpHue_TakesShortestArc(double from, double to, double k, double expected)
    {
        Assert.Equal(expected, ScreenSyncEngine.LerpHue(from, to, k) % 360, 3);
    }

    [Fact]
    public void FollowedBrightness_StaysWithinCeiling()
    {
        Assert.Equal(5, ScreenSyncEngine.FollowedBrightness(0, 100));
        Assert.Equal(100, ScreenSyncEngine.FollowedBrightness(1, 100));
        Assert.Equal(40, ScreenSyncEngine.FollowedBrightness(1, 40));
        Assert.Equal(1, ScreenSyncEngine.FollowedBrightness(0, 10));
        Assert.InRange(ScreenSyncEngine.FollowedBrightness(0.5, 100), 55, 65);
    }

    [Fact]
    public void ColorDistance_SumsChannels()
    {
        Assert.Equal(0, ScreenSyncEngine.ColorDistance(0x102030, 0x102030));
        Assert.Equal(3, ScreenSyncEngine.ColorDistance(0x102030, 0x112131));
    }
}

public class EffectsTests
{
    [Fact]
    public void AllPresets_BuildValidFlow()
    {
        foreach (var preset in Effects.All)
        {
            var parts = preset.BuildFlow(0xFF6A00, 1).Split(',');
            Assert.True(parts.Length % 4 == 0, preset.Key);
            for (var i = 0; i < parts.Length; i += 4)
            {
                Assert.True(int.Parse(parts[i]) >= 50, $"{preset.Key}: длительность");
                Assert.Equal("1", parts[i + 1]); // режим «цвет»
                Assert.True(int.Parse(parts[i + 2]) > 0, $"{preset.Key}: цвет");
                Assert.InRange(int.Parse(parts[i + 3]), 1, 100);
            }
        }
    }

    [Fact]
    public void Speed_ScalesDurations()
    {
        var breath = Effects.All.Single(e => e.Key == "breath");
        Assert.StartsWith("2200,", breath.BuildFlow(0xFF0000, 1));
        Assert.StartsWith("1100,", breath.BuildFlow(0xFF0000, 2));
        // скорость ограничена 0.25..4
        Assert.StartsWith("550,", breath.BuildFlow(0xFF0000, 100));
        Assert.StartsWith("8800,", breath.BuildFlow(0xFF0000, 0.01));
    }
}

public class AutostartTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\Lumen\\Lumen.exe\" --minimized", "C:\\Program Files\\Lumen\\Lumen.exe")]
    [InlineData("\"C:\\Lumen\\Lumen.exe\"", "C:\\Lumen\\Lumen.exe")]
    [InlineData("C:\\Lumen\\Lumen.exe --minimized", "C:\\Lumen\\Lumen.exe")]
    [InlineData("  C:\\Lumen\\Lumen.exe  ", "C:\\Lumen\\Lumen.exe")]
    public void ExeOf_ExtractsPathFromRunCommand(string command, string exe)
        => Assert.Equal(exe, Autostart.ExeOf(command));
}
