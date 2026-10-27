using System.Windows;

namespace Lumen.Views;

/// <summary>
/// Дополнительные «слоты» для шаблонов кнопок: значок (символ Segoe Fluent Icons) и значение справа
/// («Статический ›»). Так один стиль строки меню или ссылки подходит для всех пунктов.
/// </summary>
public static class Ui
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.RegisterAttached(
        "Glyph", typeof(string), typeof(Ui), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.Inherits));

    public static string GetGlyph(DependencyObject d) => (string)d.GetValue(GlyphProperty);
    public static void SetGlyph(DependencyObject d, string value) => d.SetValue(GlyphProperty, value);

    public static readonly DependencyProperty ValueProperty = DependencyProperty.RegisterAttached(
        "Value", typeof(string), typeof(Ui), new PropertyMetadata(""));

    public static string GetValue(DependencyObject d) => (string)d.GetValue(ValueProperty);
    public static void SetValue(DependencyObject d, string value) => d.SetValue(ValueProperty, value);

    /// <summary>Элемент выбран (текущий раздел, пресет, режим) — шаблон подсвечивает его акцентом.</summary>
    public static readonly DependencyProperty SelectedProperty = DependencyProperty.RegisterAttached(
        "Selected", typeof(bool), typeof(Ui), new PropertyMetadata(false));

    public static bool GetSelected(DependencyObject d) => (bool)d.GetValue(SelectedProperty);
    public static void SetSelected(DependencyObject d, bool value) => d.SetValue(SelectedProperty, value);
}
