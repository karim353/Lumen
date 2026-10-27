using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Lumen.Core;

namespace Lumen.Views;

/// <summary>
/// Движение в интерфейсе: появление карточек, переходы вкладок, «пинг» статуса, блик кнопки,
/// живые превью эффектов. Всё выключается, если в Windows отключены анимации
/// (Параметры → Специальные возможности → Визуальные эффекты) или видеокарта не тянет WPF-ускорение.
/// </summary>
public static class Motion
{
    private static readonly bool SystemAllows =
        SystemParameters.ClientAreaAnimation && (RenderCapability.Tier >> 16) >= 1;

    private static bool _energySaving;

    /// <summary>Анимации разрешены системой и не включено «Энергосбережение».</summary>
    public static bool Enabled => SystemAllows && !_energySaving;

    /// <summary>Энергосбережение: бесконечные циклы встают, сцена перестаёт «дышать».</summary>
    public static void SetEnergySaving(bool on)
    {
        if (_energySaving == on) return;
        _energySaving = on;
        Apply();
    }

    private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };

    // ---------- Фоновые циклы: 30 кадров/с и пауза, пока окно свёрнуто или в трее ----------

    /// <summary>Группа циклов, которую можно остановить отдельно (превью эффектов — пока их вкладка закрыта).</summary>
    public const string PreviewGroup = "previews";

    private static readonly List<(string Group, WeakReference<AnimationClock> Clock)> Loops = [];
    private static readonly HashSet<string> PausedGroups = [];
    private static bool _paused;

    /// <summary>
    /// Запустить бесконечную анимацию (дрейф свечения, превью эффектов, «пинг», блик) так,
    /// чтобы её можно было поставить на паузу. Каждый кадр такой анимации — перерисовка окна,
    /// поэтому частоту задаём явно: фону хватает 12–30 кадров в секунду.
    /// </summary>
    public static void RunLoop(IAnimatable target, DependencyProperty property, AnimationTimeline animation,
        int frameRate = 30, string group = "")
    {
        Timeline.SetDesiredFrameRate(animation, frameRate);
        var clock = animation.CreateClock();
        target.ApplyAnimationClock(property, clock);
        lock (Loops)
        {
            Loops.RemoveAll(l => !l.Clock.TryGetTarget(out _));
            Loops.Add((group, new WeakReference<AnimationClock>(clock)));
        }
        if (_paused || _energySaving || PausedGroups.Contains(group)) clock.Controller?.Pause();
    }

    /// <summary>Окно не видно — циклы стоят и не тратят процессор.</summary>
    public static void SetPaused(bool paused)
    {
        if (_paused == paused) return;
        _paused = paused;
        Apply();
    }

    public static void SetGroupPaused(string group, bool paused)
    {
        var changed = paused ? PausedGroups.Add(group) : PausedGroups.Remove(group);
        if (changed) Apply();
    }

    private static void Apply()
    {
        lock (Loops)
        {
            foreach (var (group, weak) in Loops)
            {
                if (!weak.TryGetTarget(out var clock) || clock.Controller == null) continue;
                if (_paused || _energySaving || PausedGroups.Contains(group)) clock.Controller.Pause();
                else clock.Controller.Resume();
            }
        }
    }

    // ---------- Появление при показе: вкладки «Цвет / Эффекты / Экран» ----------

    public static readonly DependencyProperty FadeInOnVisibleProperty = DependencyProperty.RegisterAttached(
        "FadeInOnVisible", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnFadeInOnVisibleChanged));

    public static bool GetFadeInOnVisible(DependencyObject d) => (bool)d.GetValue(FadeInOnVisibleProperty);
    public static void SetFadeInOnVisible(DependencyObject d, bool value) => d.SetValue(FadeInOnVisibleProperty, value);

    private static void OnFadeInOnVisibleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el || e.NewValue is not true) return;
        el.IsVisibleChanged += (_, args) =>
        {
            if (args.NewValue is true && el.IsLoaded) Rise(el, distance: 10, ms: 260, delayMs: 0);
        };
    }

    // ---------- Появление карточек при открытии окна, лесенкой ----------

    public static readonly DependencyProperty EntranceProperty = DependencyProperty.RegisterAttached(
        "Entrance", typeof(int), typeof(Motion), new PropertyMetadata(-1, OnEntranceChanged));

    public static int GetEntrance(DependencyObject d) => (int)d.GetValue(EntranceProperty);
    public static void SetEntrance(DependencyObject d, int value) => d.SetValue(EntranceProperty, value);

    private static void OnEntranceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el || e.NewValue is not int index || index < 0) return;
        el.Loaded += (_, _) => Rise(el, distance: 22, ms: 520, delayMs: 90 + index * 75);
    }

    /// <summary>Элемент «всплывает»: прозрачность 0→1 и сдвиг снизу вверх.</summary>
    private static void Rise(FrameworkElement el, double distance, int ms, int delayMs)
    {
        if (!Enabled) return;
        var shift = EnsureTranslate(el);
        var begin = TimeSpan.FromMilliseconds(delayMs);
        var duration = TimeSpan.FromMilliseconds(ms);

        el.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, duration) { BeginTime = begin, EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop });
        shift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(distance, 0, duration) { BeginTime = begin, EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop });

        // До старта (BeginTime) элемент уже на месте — держим его невидимым
        if (delayMs > 0)
        {
            el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimationUsingKeyFrames
            {
                FillBehavior = FillBehavior.Stop,
                KeyFrames =
                {
                    new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                    new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(begin)),
                    new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(begin + duration), EaseOut)
                }
            });
        }
    }

    private static TranslateTransform EnsureTranslate(UIElement el)
    {
        if (el.RenderTransform is TranslateTransform t && !t.IsFrozen) return t;
        t = new TranslateTransform();
        el.RenderTransform = t;
        return t;
    }

    // ---------- «Пинг»: кольцо расходится от точки статуса ----------

    public static readonly DependencyProperty PulseProperty = DependencyProperty.RegisterAttached(
        "Pulse", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnPulseChanged));

    public static bool GetPulse(DependencyObject d) => (bool)d.GetValue(PulseProperty);
    public static void SetPulse(DependencyObject d, bool value) => d.SetValue(PulseProperty, value);

    private static void OnPulseChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        var on = e.NewValue is true && Enabled;
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = el.RenderTransform as ScaleTransform ?? new ScaleTransform();
        el.RenderTransform = scale;

        if (!on)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Opacity = 0;
            return;
        }

        var period = TimeSpan.FromSeconds(2.2);
        var grow = new DoubleAnimation(1, 2.6, TimeSpan.FromSeconds(1.4))
        {
            EasingFunction = EaseOut
        };
        RunLoop(scale, ScaleTransform.ScaleXProperty, Loop(grow, period), frameRate: 24);
        RunLoop(scale, ScaleTransform.ScaleYProperty, Loop(grow, period), frameRate: 24);
        RunLoop(el, UIElement.OpacityProperty,
            Loop(new DoubleAnimation(0.55, 0, TimeSpan.FromSeconds(1.4)) { EasingFunction = EaseOut }, period), frameRate: 24);
    }

    private static AnimationTimeline Loop(DoubleAnimation inner, TimeSpan period)
    {
        var frames = new DoubleAnimationUsingKeyFrames { Duration = period, RepeatBehavior = RepeatBehavior.Forever };
        frames.KeyFrames.Add(new DiscreteDoubleKeyFrame(inner.From ?? 0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        frames.KeyFrames.Add(new EasingDoubleKeyFrame(inner.To ?? 0, KeyTime.FromTimeSpan(inner.Duration.TimeSpan), inner.EasingFunction));
        frames.KeyFrames.Add(new DiscreteDoubleKeyFrame(inner.To ?? 0, KeyTime.FromTimeSpan(period)));
        return frames;
    }

    // ---------- Блик, пробегающий по кнопке синхронизации ----------

    public static readonly DependencyProperty ShimmerProperty = DependencyProperty.RegisterAttached(
        "Shimmer", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnShimmerChanged));

    public static bool GetShimmer(DependencyObject d) => (bool)d.GetValue(ShimmerProperty);
    public static void SetShimmer(DependencyObject d, bool value) => d.SetValue(ShimmerProperty, value);

    private static void OnShimmerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        var shift = EnsureTranslate(el);
        if (e.NewValue is not true || !Enabled)
        {
            shift.BeginAnimation(TranslateTransform.XProperty, null);
            el.Visibility = Visibility.Collapsed;
            return;
        }

        el.Visibility = Visibility.Visible;
        void Start()
        {
            var width = (el.Parent as FrameworkElement)?.ActualWidth ?? 400;
            var run = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(2.6), RepeatBehavior = RepeatBehavior.Forever };
            run.KeyFrames.Add(new DiscreteDoubleKeyFrame(-el.Width, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            run.KeyFrames.Add(new EasingDoubleKeyFrame(width, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.3)),
                new SineEase { EasingMode = EasingMode.EaseInOut }));
            run.KeyFrames.Add(new DiscreteDoubleKeyFrame(width, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.6))));
            RunLoop(shift, TranslateTransform.XProperty, run);
        }
        if (el.IsLoaded) Start();
        else el.Loaded += (_, _) => Start();
    }
}

/// <summary>
/// Живое превью эффекта на плитке: кисть сама проигрывает шаги color flow — тот же ритм,
/// что крутит лампа. Одна кисть на эффект, чтобы полоска и подсветка плитки шли в такт.
/// </summary>
public sealed class EffectPreviewBrushConverter : System.Windows.Data.IValueConverter
{
    private static readonly ConditionalWeakTable<EffectPreset, SolidColorBrush> Brushes = new();

    // Базовый цвет для «Дыхания», «Пульса» и «Строба» в превью — тёплый янтарный, как у лампы по умолчанию
    private const int PreviewBase = 0xFF8A2A;

    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => value is EffectPreset preset ? Brushes.GetValue(preset, Create) : System.Windows.Media.Brushes.Transparent;

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();

    private static SolidColorBrush Create(EffectPreset preset)
    {
        var steps = preset.Steps(PreviewBase);
        var brush = new SolidColorBrush(ToColor(steps[^1]));
        if (!Motion.Enabled || steps.Count == 0) return brush;

        // Строб слишком резкий для интерфейса — в превью вдвое медленнее
        var stretch = preset.Key == "strobe" ? 4.0 : 1.0;
        var frames = new ColorAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        var t = TimeSpan.Zero;
        frames.KeyFrames.Add(new DiscreteColorKeyFrame(ToColor(steps[^1]), KeyTime.FromTimeSpan(t)));
        foreach (var step in steps)
        {
            t += TimeSpan.FromMilliseconds(Math.Max(50, step.DurationMs) * stretch);
            frames.KeyFrames.Add(new LinearColorKeyFrame(ToColor(step), KeyTime.FromTimeSpan(t)));
        }
        frames.Duration = t;
        Motion.RunLoop(brush, SolidColorBrush.ColorProperty, frames, frameRate: 24, group: Motion.PreviewGroup);
        return brush;
    }

    /// <summary>Яркость шага — множителем к цвету; совсем в ноль не гасим, иначе плитка «пропадает».</summary>
    private static Color ToColor(FlowStep step)
    {
        var (r, g, b) = ColorMath.FromYeelightRgb(step.Rgb);
        var k = 0.18 + 0.82 * Math.Clamp(step.Brightness, 1, 100) / 100.0;
        return Color.FromRgb((byte)(r * k), (byte)(g * k), (byte)(b * k));
    }
}
