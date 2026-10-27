using System.Globalization;
using System.Text;

namespace Lumen.Core;

/// <summary>Один шаг color flow: длительность перехода, цвет, яркость.</summary>
public readonly record struct FlowStep(int DurationMs, int Rgb, int Brightness);

/// <summary>
/// Эффект подсветки = color flow, который лампа крутит сама (bg_start_cf).
/// ПК после запуска ничего не шлёт — работает даже при закрытом приложении.
/// </summary>
public sealed class EffectPreset
{
    private readonly Func<int, IReadOnlyList<FlowStep>> _build;

    public EffectPreset(string key, string name, string glyph, Func<int, IReadOnlyList<FlowStep>> build)
    {
        Key = key;
        Name = name;
        Glyph = glyph;
        _build = build;
    }

    public string Key { get; }
    public string Name { get; }
    public string Glyph { get; }

    /// <summary>Шаги эффекта для заданного базового цвета — для анимированного превью в UI.</summary>
    public IReadOnlyList<FlowStep> Steps(int baseRgb) => _build(baseRgb);

    /// <summary>Строка flow для протокола: "duration,mode,value,brightness,..." (mode 1 = цвет).</summary>
    public string BuildFlow(int baseRgb, double speed)
    {
        speed = Math.Clamp(speed, 0.25, 4);
        var sb = new StringBuilder();
        foreach (var step in _build(baseRgb))
        {
            if (sb.Length > 0) sb.Append(',');
            var duration = Math.Max(50, (int)(step.DurationMs / speed));
            sb.Append(CultureInfo.InvariantCulture,
                $"{duration},1,{Math.Max(1, step.Rgb)},{Math.Clamp(step.Brightness, 1, 100)}");
        }
        return sb.ToString();
    }
}

public static class Effects
{
    private const int Red = 0xFF0000, Orange = 0xFF6A00, Yellow = 0xFFD000, Green = 0x00FF40,
        Cyan = 0x00E5FF, Blue = 0x0040FF, Purple = 0x8A00FF, Pink = 0xFF00A0;

    public static IReadOnlyList<EffectPreset> All { get; } =
    [
        new("breath", "Дыхание", "◐", c =>
        [
            new(2200, c, 100),
            new(2200, c, 4)
        ]),

        new("rainbow", "Радуга", "◌", _ =>
        [
            new(1800, Red, 100), new(1800, Orange, 100), new(1800, Yellow, 100), new(1800, Green, 100),
            new(1800, Cyan, 100), new(1800, Blue, 100), new(1800, Purple, 100), new(1800, Pink, 100)
        ]),

        new("pulse", "Пульс", "◉", c =>
        [
            new(180, c, 100),
            new(700, c, 8),
            new(180, c, 100),
            new(1400, c, 8)
        ]),

        new("candle", "Свеча", "▲", _ =>
        [
            new(800, 0xFF7A1A, 60), new(400, 0xFF6A10, 45), new(900, 0xFF8A2A, 70),
            new(300, 0xFF7015, 40), new(700, 0xFF801F, 65), new(500, 0xFF6510, 50)
        ]),

        new("sunset", "Закат", "◒", _ =>
        [
            new(5000, 0xFF8A00, 90), new(5000, 0xFF3A1A, 75), new(5000, 0xC0186A, 60),
            new(5000, 0x5A1AA0, 50), new(5000, 0xFF3A1A, 75)
        ]),

        new("ocean", "Океан", "≈", _ =>
        [
            new(4000, 0x0050FF, 80), new(4000, 0x00B4FF, 60), new(4000, 0x00FFC8, 70),
            new(4000, 0x0030C0, 50)
        ]),

        new("neon", "Неон", "✦", _ =>
        [
            new(1200, 0xFF00C8, 100), new(1200, 0x00F0FF, 100)
        ]),

        new("strobe", "Строб", "✳", c =>
        [
            new(50, c, 100),
            new(150, c, 1)
        ])
    ];
}
