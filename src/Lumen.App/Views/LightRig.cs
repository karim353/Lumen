using System;
using System.Windows;
using System.Windows.Media;
using Lumen.Core;
using Lumen.ViewModels;

namespace Lumen.Views;

/// <summary>
/// «Свет» интерфейса: плавно догоняет то, что сейчас светит лампа (основной свет по температуре
/// и яркости, подсветка слева/справа). Из него берут цвета 3D-сцена, фоновое свечение окна
/// и кнопка синхронизации. Состояние читается из модели каждый кадр, пока окно видно.
/// </summary>
public sealed class LightRig : DependencyObject
{
    public static readonly DependencyProperty MainColorProperty = Register(nameof(MainColor), Colors.Black);
    public static readonly DependencyProperty BackLeftProperty = Register(nameof(BackLeft), Colors.Black);
    public static readonly DependencyProperty BackRightProperty = Register(nameof(BackRight), Colors.Black);

    /// <summary>Те же цвета, но прозрачность = насколько ярко горит (для свечений).</summary>
    public static readonly DependencyProperty MainGlowProperty = Register(nameof(MainGlow), Colors.Transparent);
    public static readonly DependencyProperty BackLeftGlowProperty = Register(nameof(BackLeftGlow), Colors.Transparent);
    public static readonly DependencyProperty BackRightGlowProperty = Register(nameof(BackRightGlow), Colors.Transparent);

    public Color MainColor { get => (Color)GetValue(MainColorProperty); private set => SetValue(MainColorProperty, value); }
    public Color BackLeft { get => (Color)GetValue(BackLeftProperty); private set => SetValue(BackLeftProperty, value); }
    public Color BackRight { get => (Color)GetValue(BackRightProperty); private set => SetValue(BackRightProperty, value); }
    public Color MainGlow { get => (Color)GetValue(MainGlowProperty); private set => SetValue(MainGlowProperty, value); }
    public Color BackLeftGlow { get => (Color)GetValue(BackLeftGlowProperty); private set => SetValue(BackLeftGlowProperty, value); }
    public Color BackRightGlow { get => (Color)GetValue(BackRightGlowProperty); private set => SetValue(BackRightGlowProperty, value); }

    /// <summary>0..1 — насколько горит основной свет и подсветка (для 3D-сцены).</summary>
    public double MainLevel { get; private set; }
    public double BackLevel { get; private set; }

    /// <summary>Модель — сцене нужна миниатюра экрана во время синхронизации.</summary>
    public MainViewModel? Model => _vm;

    /// <summary>Каждый кадр после пересчёта — 3D-сцена обновляет материалы и поворот.</summary>
    public event Action<double>? Frame;

    private MainViewModel? _vm;
    private Window? _window;
    private bool _running;
    private TimeSpan _lastTime;

    // Текущие (сглаженные) значения в линейных числах — так переходы плавные
    private Vec _main, _left, _right;
    private double _mainLevel, _backLevel;
    private bool _first = true;

    public void Attach(Window window, MainViewModel vm)
    {
        _window = window;
        _vm = vm;
        window.IsVisibleChanged += (_, _) => UpdateRunning();
        window.StateChanged += (_, _) => UpdateRunning();
        UpdateRunning();
    }

    /// <summary>Свёрнутое или спрятанное в трей окно не рисуем — ни кадров, ни нагрузки.</summary>
    private void UpdateRunning()
    {
        var shouldRun = _window is { IsVisible: true } w && w.WindowState != WindowState.Minimized;
        if (shouldRun == _running) return;
        _running = shouldRun;
        if (shouldRun)
        {
            _lastTime = TimeSpan.Zero;
            CompositionTarget.Rendering += OnRendering;
        }
        else
        {
            CompositionTarget.Rendering -= OnRendering;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_vm == null) return;
        var now = e is RenderingEventArgs r ? r.RenderingTime : TimeSpan.Zero;
        // 24 кадра в секунду: 3D-освещение WPF считает на процессоре, а медленному облёту камеры и плавному свету этого хватает
        if (!_first && (now - _lastTime).TotalSeconds < 1 / 25.0) return;
        var dt = _lastTime == TimeSpan.Zero ? 1 / 24.0 : Math.Clamp((now - _lastTime).TotalSeconds, 0, 0.1);
        _lastTime = now;

        var vm = _vm;
        var (mr, mg, mb) = ColorMath.KelvinToRgb(vm.MainCt);
        var mainTarget = new Vec(mr, mg, mb);
        var mainLevel = vm.MainPower ? 0.25 + 0.75 * Math.Clamp(vm.MainBright, 1, 100) / 100 : 0;
        var backLevel = vm.BgPower ? 0.3 + 0.7 * Math.Clamp(vm.BgBright, 1, 100) / 100 : 0;
        var leftTarget = Vec.From(vm.BackLeftColor);
        var rightTarget = Vec.From(vm.BackRightColor);

        // Во время синхронизации догоняем быстро — превью не должно отставать от лампы
        var rate = vm.IsSyncing || vm.IsTestingZones ? 16 : 6;
        var k = _first || !Motion.Enabled ? 1 : 1 - Math.Exp(-dt * rate);
        _first = false;

        _main = Vec.Lerp(_main, mainTarget, k);
        _left = Vec.Lerp(_left, leftTarget, k);
        _right = Vec.Lerp(_right, rightTarget, k);
        _mainLevel += (mainLevel - _mainLevel) * k;
        _backLevel += (backLevel - _backLevel) * k;

        MainLevel = _mainLevel;
        BackLevel = _backLevel;
        SetIfChanged(MainColorProperty, _main.ToColor(255));
        SetIfChanged(BackLeftProperty, _left.ToColor(255));
        SetIfChanged(BackRightProperty, _right.ToColor(255));
        SetIfChanged(MainGlowProperty, _main.ToColor(_mainLevel * 255));
        SetIfChanged(BackLeftGlowProperty, _left.ToColor(_backLevel * 255));
        SetIfChanged(BackRightGlowProperty, _right.ToColor(_backLevel * 255));

        Frame?.Invoke(now.TotalSeconds);
    }

    private void SetIfChanged(DependencyProperty dp, Color value)
    {
        if ((Color)GetValue(dp) != value) SetValue(dp, value);
    }

    private static DependencyProperty Register(string name, Color initial)
        => DependencyProperty.Register(name, typeof(Color), typeof(LightRig), new PropertyMetadata(initial));

    private readonly record struct Vec(double R, double G, double B)
    {
        public static Vec From(Color c) => new(c.R, c.G, c.B);
        public static Vec Lerp(Vec a, Vec b, double k) => new(a.R + (b.R - a.R) * k, a.G + (b.G - a.G) * k, a.B + (b.B - a.B) * k);

        public Color ToColor(double alpha) => Color.FromArgb(Byte(alpha), Byte(R), Byte(G), Byte(B));

        private static byte Byte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 255));
    }
}
