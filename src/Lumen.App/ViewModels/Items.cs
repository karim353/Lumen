using System.Collections.Generic;
using System.Windows.Media;
using Lumen.Core;

namespace Lumen.ViewModels;

public enum BackMode { Color, Effect, Screen }

public sealed class DeviceItem(string host, string name, string model) : ObservableObject
{
    // Коды моделей из discovery → то, что написано на коробке
    private static readonly Dictionary<string, string> ModelNames = new()
    {
        ["lamp15"] = "Screen Light Bar Pro",
        ["lamp1"] = "Desk Lamp",
        ["color"] = "Color Bulb",
        ["mono"] = "White Bulb",
        ["stripe"] = "Lightstrip",
        ["strip6"] = "Lightstrip Plus",
        ["ceiling"] = "Ceiling Light",
        ["bslamp1"] = "Bedside Lamp",
        ["bslamp2"] = "Bedside Lamp 2"
    };

    public string Host { get; } = host;
    public string Name { get; } = name;
    public string Model { get; } = model;

    public string Title => !string.IsNullOrWhiteSpace(Name) ? Name
        : ModelNames.TryGetValue(Model, out var pretty) ? pretty
        : string.IsNullOrWhiteSpace(Model) ? "Лампа" : Model;

    /// <summary>Название модели «с коробки», независимо от имени, которое дал пользователь.</summary>
    public string ModelName => ModelNames.TryGetValue(Model, out var pretty) ? pretty
        : string.IsNullOrWhiteSpace(Model) ? "Лампа Yeelight" : Model;

    public string Subtitle => Host;
}

public sealed class SwatchItem
{
    public SwatchItem(string name, double hue, double sat)
    {
        Name = name;
        Hue = hue;
        Sat = sat;
        var (r, g, b) = ColorMath.HsvToRgb(hue, sat / 100.0, 1);
        Brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        Brush.Freeze();
    }

    public string Name { get; }
    public double Hue { get; }
    public double Sat { get; }
    public SolidColorBrush Brush { get; }
}

public sealed class EffectItem(EffectPreset preset) : ObservableObject
{
    private bool _isActive;

    public EffectPreset Preset { get; } = preset;
    public string Name => Preset.Name;
    public string Glyph => Preset.Glyph;

    public bool IsActive
    {
        get => _isActive;
        set => SetField(ref _isActive, value);
    }
}

public sealed class MonitorItem(DisplayMonitor monitor, int number) : ObservableObject
{
    private bool _isSelected;

    public string DeviceName { get; } = monitor.DeviceName;
    public bool IsPrimary { get; } = monitor.IsPrimary;
    public string Title { get; } = $"Экран {number}";
    public string Subtitle { get; } =
        $"{monitor.Bounds.Width}×{monitor.Bounds.Height}{(monitor.IsPrimary ? " · основной" : "")}";

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }
}
