using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;

namespace Lumen.Views;

/// <summary>
/// 3D-сцена в шапке окна: рабочее место с монитором и Screen Light Bar Pro.
///
/// Свет настоящий: фронтальный LED — прожектор (SpotLight), он рисует тёплое пятно на столе
/// и освещает подставку и пульт; задняя RGB-полоса — два цветных PointLight, которые заливают
/// стену и стол за монитором. Стена и стол — плотные сетки: WPF считает освещение по вершинам.
/// Поверх 3D — свечение (bloom) вокруг LED и пылинки в свете подсветки, которых WPF 3D сам не умеет.
///
/// Камера облетает сцену: медленное «дыхание», поворот за мышью и наезд при открытии.
/// Во время синхронизации экран монитора показывает миниатюру настоящего экрана.
/// </summary>
public sealed class LampScene : Grid
{
    // ---------- Камера ----------
    // Цель камеры ниже центра модели: монитор стоит в верхней части шапки, внизу остаётся место под подписи
    private static readonly Point3D Target = new(0, -2.2, 2.5);
    private const double Distance = 47;
    private const double BaseYaw = -14, BasePitch = 9;     // почти анфас, чуть сбоку и сверху — как фото рабочего места
    private const double IdleYaw = 5, IdlePitch = 2;       // «дыхание»
    private const double MouseYaw = 18, MousePitch = 7;    // поворот за мышью
    private const double IntroSeconds = 2.2;

    // ---------- Геометрия (единицы ≈ 3.7 см) ----------
    private const double DeskY = -8.1, WallZ = -9;
    private const double BarY = 5.05, BarZ = 0.35, BarHalf = 6.3;
    private const double LedHalf = 5.9;
    private const double PuckX = 10.4, PuckZ = -2.4, PuckR = 1.15, PuckH = 0.55;
    private const double MatY = DeskY + 0.08;              // верх коврика: на нём клавиатура и мышь
    private const double VerticalFov = 26;                 // градусы; горизонтальный считается от ширины шапки

    private readonly Viewport3D _viewport;
    // Узкий диапазон глубины: при дистанции ~45 точности буфера глубины хватает, чтобы стекло экрана не «мерцало» сквозь корпус
    private readonly PerspectiveCamera _camera = new() { FieldOfView = 34, NearPlaneDistance = 8, FarPlaneDistance = 320 };
    private readonly ModelVisual3D _world = new();

    // Динамические кисти и источники света
    private readonly SolidColorBrush _frontLed = new(Colors.Black);
    private readonly LinearGradientBrush _backLed = Horizontal();
    private readonly SolidColorBrush _ring = new(Colors.Black);
    private readonly RadialGradientBrush _wallHotLeft = Radial(), _wallHotRight = Radial(), _deskPool = Radial();
    private readonly LinearGradientBrush _beam = new()
    {
        StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1),
        GradientStops = { new GradientStop(Colors.Transparent, 0), new GradientStop(Colors.Transparent, 1) }
    };
    private readonly SpotLight _spot = new(Colors.Black, new Point3D(0, 4.6, 1.05), new Vector3D(0, -1, 0.62), 110, 46)
    {
        ConstantAttenuation = 0.55, LinearAttenuation = 0.035
    };
    private readonly PointLight _pointLeft = new(Colors.Black, new Point3D(-4.6, 5.3, -1.9))
    {
        ConstantAttenuation = 0.35, LinearAttenuation = 0.05, QuadraticAttenuation = 0.0015
    };
    private readonly PointLight _pointRight = new(Colors.Black, new Point3D(4.6, 5.3, -1.9))
    {
        ConstantAttenuation = 0.35, LinearAttenuation = 0.05, QuadraticAttenuation = 0.0015
    };

    // Экран монитора: «обои» или живая миниатюра во время синхронизации
    private readonly EmissiveMaterial _screenLight = new();
    private readonly RadialGradientBrush _wallpaperLeft = Radial(), _wallpaperRight = Radial();
    private readonly DrawingBrush _wallpaper;
    private WriteableBitmap? _thumb;
    private ImageBrush? _thumbBrush;
    private int _thumbVersion = -1;

    // 2D поверх 3D
    private readonly Canvas _fx = new() { IsHitTestVisible = false };
    // Свечение рисуется градиентами, а не BlurEffect: оно двигается вместе с камерой каждый кадр,
    // и размытие такой площади стоило заметную долю процессора
    private readonly Rectangle _bloomCore = new() { RadiusX = 4, RadiusY = 4 };
    private readonly Rectangle _bloomHalo = new() { RadiusX = 18, RadiusY = 18 };
    private readonly Ellipse _ringGlow = new();
    private readonly LinearGradientBrush _bloomCoreBrush = Glow3(), _bloomHaloBrush = Glow3();
    private readonly RadialGradientBrush _ringGlowBrush = Radial();
    private readonly RotateTransform _bloomCoreTurn = new(), _bloomHaloTurn = new();
    private readonly List<Mote> _motes = [];

    private LightRig? _rig;
    private Point _mouseTarget, _mouse;
    private double _lastT, _introStart = -1;

    public LampScene()
    {
        ClipToBounds = true;
        Background = Brushes.Transparent; // чтобы ловить мышь по всей шапке

        // Сцена растворяется в интерфейсе сверху и снизу — без жёсткой границы
        OpacityMask = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, 0, 0, 0), 0),
                new GradientStop(Color.FromArgb(255, 0, 0, 0), 0.16),
                new GradientStop(Color.FromArgb(255, 0, 0, 0), 0.62),
                new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.97)
            }
        };

        _wallpaper = BuildWallpaper();
        _screenLight.Brush = _wallpaper;

        _viewport = new Viewport3D { IsHitTestVisible = false, ClipToBounds = true, Camera = _camera };
        _viewport.Children.Add(new ModelVisual3D { Content = BuildLights() });
        _world.Content = Mesh3D.MergeByMaterial(BuildWorld());
        _viewport.Children.Add(_world);
        Children.Add(_viewport);

        _bloomHalo.Fill = _bloomHaloBrush;
        _bloomCore.Fill = _bloomCoreBrush;
        _ringGlow.Fill = _ringGlowBrush;
        _bloomHalo.RenderTransform = _bloomHaloTurn;
        _bloomCore.RenderTransform = _bloomCoreTurn;
        _fx.Children.Add(_bloomHalo);
        _fx.Children.Add(_bloomCore);
        _fx.Children.Add(_ringGlow);
        var random = new Random(7);
        for (var i = 0; i < 22; i++)
        {
            var mote = new Mote(random);
            _motes.Add(mote);
            _fx.Children.Add(mote.Shape);
        }
        Children.Add(_fx);

        MouseEnter += (_, _) =>
        {
            _mouseOver = true;
            StartFast();
        };
        MouseMove += (_, e) =>
        {
            var p = e.GetPosition(this);
            _mouseTarget = new Point(
                Math.Clamp(p.X / Math.Max(1, ActualWidth) * 2 - 1, -1, 1),
                Math.Clamp(p.Y / Math.Max(1, ActualHeight) * 2 - 1, -1, 1));
            StartFast();
        };
        MouseLeave += (_, _) =>
        {
            _mouseOver = false;
            _mouseTarget = new Point(0, 0); // камера плавно возвращается, быстрый режим кончится, когда она встанет
        };
        Loaded += (_, _) =>
        {
            AttachRig();
            StartFast(); // наезд камеры при открытии — тоже с частотой экрана
        };
        SizeChanged += (_, _) => UpdateFieldOfView();
        Unloaded += (_, _) =>
        {
            StopFast();
            if (_rig != null) _rig.Frame -= OnFrame;
            _rig = null;
        };
        UpdateCamera(0, 0, 1);
    }

    /// <summary>
    /// FieldOfView у WPF — горизонтальный. Держим постоянным вертикальный угол: в широкой шапке
    /// видно больше стола по сторонам, а монитор остаётся того же размера по высоте.
    /// </summary>
    private void UpdateFieldOfView()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var aspect = ActualWidth / ActualHeight;
        _camera.FieldOfView = 2 * Math.Atan(Math.Tan(VerticalFov / 2 * Math.PI / 180) * aspect) * 180 / Math.PI;
    }

    private void AttachRig()
    {
        if (_rig != null || TryFindResource("Rig") is not LightRig rig) return;
        _rig = rig;
        rig.Frame += OnFrame;
    }

    // ================= Кадр =================

    // Последний свет лампы: свечение поверх 3D двигается вместе с камерой и рисуется этими цветами
    private Color _main, _left, _right;
    private double _mainLevel, _backLevel;
    private bool _connected;

    // «Быстрый» режим: камера обновляется с частотой экрана, пока мышь над шапкой, камера догоняет её
    // или идёт наезд при открытии. В покое хватает 24 кадров от LightRig — «дыхание» медленное.
    private bool _fast, _mouseOver;
    private TimeSpan _lastRender;

    /// <summary>Кадр LightRig (24/с): свет и цвета; камеру — только если не идёт быстрый режим.</summary>
    private void OnFrame(double t)
    {
        if (_rig == null || !IsVisible) return; // открыт другой раздел — сцену не видно, не тратим кадры
        UpdateLights();
        if (!_fast) UpdateView(t);
    }

    /// <summary>Кадр экрана в быстром режиме: камера, свечение и пылинки без рывков.</summary>
    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs r || r.RenderingTime == _lastRender) return; // WPF шлёт событие и дважды за кадр
        _lastRender = r.RenderingTime;
        if (_rig == null || !IsVisible)
        {
            StopFast();
            return;
        }
        var t = r.RenderingTime.TotalSeconds; // тот же отсчёт, что у LightRig
        UpdateView(t);
        var settled = (_mouseTarget - _mouse).Length < 0.002 && (!Motion.Enabled || t - _introStart >= IntroSeconds);
        if (!_mouseOver && settled) StopFast();
    }

    private void StartFast()
    {
        if (_fast || _rig == null) return;
        _fast = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopFast()
    {
        if (!_fast) return;
        _fast = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    /// <summary>Камера (наезд → «дыхание» + мышь с инерцией) и всё, что к ней привязано на экране.</summary>
    private void UpdateView(double t)
    {
        if (_introStart < 0) _introStart = t;
        var dt = _lastT == 0 ? 1 / 60.0 : Math.Clamp(t - _lastT, 0, 0.1);
        _lastT = t;

        var k = Motion.Enabled ? 1 - Math.Exp(-dt * 4.5) : 1;
        _mouse = new Point(_mouse.X + (_mouseTarget.X - _mouse.X) * k, _mouse.Y + (_mouseTarget.Y - _mouse.Y) * k);
        var idle = Motion.Enabled ? t : 0;
        var intro = Motion.Enabled ? EaseOutCubic(Math.Clamp((t - _introStart) / IntroSeconds, 0, 1)) : 1;
        var yaw = IdleYaw * Math.Sin(idle * 0.36) + MouseYaw * _mouse.X;
        var pitch = IdlePitch * Math.Sin(idle * 0.27 + 1.2) - MousePitch * _mouse.Y;
        UpdateCamera(yaw, pitch, intro);
        UpdateOverlay(t, _main, _left, _right, _mainLevel, _backLevel, _connected);
    }

    /// <summary>Свет лампы: материалы светодиодов, источники света, пятна на стене и столе, экран.</summary>
    private void UpdateLights()
    {
        var main = _main = _rig!.MainColor;
        var left = _left = _rig.BackLeft;
        var right = _right = _rig.BackRight;
        var mainLevel = _mainLevel = _rig.MainLevel;
        var backLevel = _backLevel = _rig.BackLevel;
        var connected = _connected = _rig.Model?.IsConnected == true;

        _frontLed.Color = Mix(Color.FromRgb(0x22, 0x22, 0x26), Lift(main), mainLevel);
        _backLed.GradientStops[0].Color = Mix(Color.FromRgb(0x14, 0x14, 0x18), left, backLevel);
        _backLed.GradientStops[1].Color = Mix(Color.FromRgb(0x14, 0x14, 0x18), right, backLevel);
        _ring.Color = connected
            ? Mix(Color.FromRgb(0x30, 0x30, 0x36), Mix(Colors.White, main, 0.5), 0.35 + 0.65 * mainLevel)
            : Color.FromRgb(0x24, 0x24, 0x28);

        _spot.Color = Scale(main, 0.95 * mainLevel);
        _pointLeft.Color = Scale(left, backLevel);
        _pointRight.Color = Scale(right, backLevel);

        SetRadial(_wallHotLeft, left, 0.78 * backLevel);
        SetRadial(_wallHotRight, right, 0.78 * backLevel);
        SetRadial(_deskPool, main, 0.32 * mainLevel);
        _beam.GradientStops[0].Color = Color.FromArgb((byte)(16 * mainLevel), main.R, main.G, main.B);
        _beam.GradientStops[1].Color = Color.FromArgb(0, main.R, main.G, main.B);

        UpdateScreen(left, right, backLevel);
    }

    /// <summary>Камера на сфере вокруг монитора; при открытии — издалека, сверху и сбоку.</summary>
    private void UpdateCamera(double yaw, double pitch, double intro)
    {
        var distance = Distance + (1 - intro) * 34;
        var yawDeg = BaseYaw + yaw - (1 - intro) * 24;
        var pitchDeg = BasePitch + pitch + (1 - intro) * 14;
        double ya = yawDeg * Math.PI / 180, pa = pitchDeg * Math.PI / 180;
        var dir = new Vector3D(Math.Sin(ya) * Math.Cos(pa), Math.Sin(pa), Math.Cos(ya) * Math.Cos(pa));
        _camera.Position = Target + dir * distance;
        _camera.LookDirection = -dir;
        _camera.UpDirection = new Vector3D(0, 1, 0);
    }

    /// <summary>Экран: во время синхронизации — миниатюра настоящего экрана, иначе «обои» в цветах подсветки.</summary>
    private void UpdateScreen(Color left, Color right, double backLevel)
    {
        var model = _rig?.Model;
        var thumb = model?.SyncThumbnail;
        if (thumb != null)
        {
            var version = model!.SyncThumbnailVersion;
            if (version != _thumbVersion)
            {
                _thumbVersion = version;
                if (_thumb == null || _thumb.PixelWidth != thumb.Width || _thumb.PixelHeight != thumb.Height)
                {
                    _thumb = new WriteableBitmap(thumb.Width, thumb.Height, 96, 96, PixelFormats.Bgr32, null);
                    _thumbBrush = new ImageBrush(_thumb) { Stretch = Stretch.Fill };
                    RenderOptions.SetBitmapScalingMode(_thumbBrush, BitmapScalingMode.Linear);
                }
                _thumb.WritePixels(new Int32Rect(0, 0, thumb.Width, thumb.Height), thumb.Bgra, thumb.Width * 4, 0);
            }
            if (_screenLight.Brush != _thumbBrush) _screenLight.Brush = _thumbBrush;
            return;
        }

        _thumbVersion = -1;
        if (_screenLight.Brush != _wallpaper) _screenLight.Brush = _wallpaper;
        // Экран чуть подкрашен светом подсветки, как отражение в стекле
        SetRadial(_wallpaperLeft, left, 0.10 + 0.14 * backLevel);
        SetRadial(_wallpaperRight, right, 0.10 + 0.14 * backLevel);
    }

    /// <summary>Свечение вокруг LED, ореол пульта и пылинки — проекция 3D-точек на экран.</summary>
    private void UpdateOverlay(double t, Color main, Color left, Color right, double mainLevel, double backLevel, bool connected)
    {
        if (_world.TransformToAncestor(_viewport) is not { } to2D) return;

        // Bloom фронтального LED: вытянутое пятно вдоль полосы
        if (to2D.TryTransform(new Point3D(-LedHalf, BarY - 0.3, BarZ + 0.45), out var a)
            && to2D.TryTransform(new Point3D(LedHalf, BarY - 0.3, BarZ + 0.45), out var b))
        {
            PlaceStreak(_bloomCore, _bloomCoreTurn, a, b, 7, 12);
            PlaceStreak(_bloomHalo, _bloomHaloTurn, a, b, 30, 40);
            var c = Lift(main);
            SetGlow3(_bloomCoreBrush, c, 230 * mainLevel);
            SetGlow3(_bloomHaloBrush, c, 95 * mainLevel);
        }

        if (to2D.TryTransform(new Point3D(PuckX, DeskY + PuckH + 0.02, PuckZ), out var ring))
        {
            _ringGlow.Width = 44;
            _ringGlow.Height = 18;
            Canvas.SetLeft(_ringGlow, ring.X - 22);
            Canvas.SetTop(_ringGlow, ring.Y - 9);
            SetRadial(_ringGlowBrush, _ring.Color, connected ? (90 + 110 * mainLevel) / 255 : 0);
        }

        // Пылинки в свете подсветки: всплывают над Light Bar и гаснут
        if (to2D.TryTransform(new Point3D(0, BarY + 2.5, WallZ + 4), out var cloud))
        {
            var width = ActualWidth;
            foreach (var mote in _motes)
                mote.Update(Motion.Enabled ? t : 0, cloud, width, left, right, backLevel);
        }
    }

    private static void PlaceStreak(Rectangle r, RotateTransform turn, Point a, Point b, double height, double extra)
    {
        var length = (b - a).Length + extra;
        var mid = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        r.Width = length;
        r.Height = height;
        Canvas.SetLeft(r, mid.X - length / 2);
        Canvas.SetTop(r, mid.Y - height / 2);
        turn.CenterX = length / 2;
        turn.CenterY = height / 2;
        turn.Angle = Math.Atan2(b.Y - a.Y, b.X - a.X) * 180 / Math.PI;
    }

    // ================= Мир =================

    private Model3DGroup BuildLights() => new()
    {
        Children =
        {
            new AmbientLight(Color.FromRgb(0x26, 0x26, 0x2C)),
            new DirectionalLight(Color.FromRgb(0x4C, 0x4C, 0x58), new Vector3D(-0.45, -0.55, -1)),   // ключевой: слева-сверху
            // Пять источников вместо восьми: заполняющий и боковые цветные почти не видны,
            // а каждый источник — это расчёт для каждой вершины сцены на процессоре
            _spot, _pointLeft, _pointRight
        }
    };

    private Model3DGroup BuildWorld()
    {
        var wall = Matte(Color.FromRgb(0x6A, 0x6A, 0x72), 0, 1);
        var desk = Matte(Color.FromRgb(0x4C, 0x4C, 0x54), 0x22, 18);
        var body = Matte(Color.FromRgb(0x18, 0x19, 0x1E), 0x55, 40);
        var stand = Matte(Color.FromRgb(0x26, 0x27, 0x2D), 0x60, 50);
        var alu = Matte(Color.FromRgb(0x3E, 0x3F, 0x46), 0xA0, 70);
        var puck = Matte(Color.FromRgb(0x2C, 0x2D, 0x33), 0x80, 60);

        var g = new Model3DGroup();

        // --- Комната: стена и стол. В центре — частые сетки (на них видно пятна света), по краям — крупные,
        // чтобы при облёте камеры нигде не показался край комнаты ---
        g.Children.Add(Model(Mesh3D.Grid(new Point3D(-50, DeskY, WallZ), new Vector3D(100, 0, 0), new Vector3D(0, 42, 0), 40, 16), wall));
        g.Children.Add(Model(Mesh3D.Grid(new Point3D(-50, DeskY, 36), new Vector3D(100, 0, 0), new Vector3D(0, 0, WallZ - 36), 40, 20), desk));
        foreach (var side in new[] { -1, 1 })
        {
            var x0 = side < 0 ? -260 : 50;
            g.Children.Add(Model(Mesh3D.Grid(new Point3D(x0, DeskY, WallZ), new Vector3D(210, 0, 0), new Vector3D(0, 42, 0), 12, 6), wall));
            g.Children.Add(Model(Mesh3D.Grid(new Point3D(x0, DeskY, 36), new Vector3D(210, 0, 0), new Vector3D(0, 0, WallZ - 36), 12, 6), desk));
        }
        g.Children.Add(Model(Mesh3D.Grid(new Point3D(-260, DeskY + 42, WallZ), new Vector3D(520, 0, 0), new Vector3D(0, 80, 0), 12, 4), wall));
        g.Children.Add(Model(Mesh3D.Grid(new Point3D(-260, DeskY, 200), new Vector3D(520, 0, 0), new Vector3D(0, 0, -164), 12, 6), desk));

        // Пятна света — поверх стены и стола (прозрачные «декали»)
        g.Children.Add(Glow(WallQuad(-24, 16, WallZ + 0.05), _wallHotLeft));
        g.Children.Add(Glow(WallQuad(-16, 24, WallZ + 0.06), _wallHotRight));
        g.Children.Add(Glow(Mesh3D.Quad(new Point3D(-14, DeskY + 0.02, 14), new Point3D(14, DeskY + 0.02, 14),
            new Point3D(14, DeskY + 0.02, -1), new Point3D(-14, DeskY + 0.02, -1), new Vector3D(0, 1, 0)), _deskPool));

        // Контактные тени: под подставкой, под монитором, под пультом
        g.Children.Add(Shadow(new Rect(-5.5, -4.2, 11, 6.6), DeskY + 0.03, 0.75));
        g.Children.Add(Shadow(new Rect(-10, -3.5, 20, 4.2), DeskY + 0.025, 0.35));
        g.Children.Add(Shadow(new Rect(PuckX - 2, PuckZ - 1.9, 4, 3.8), DeskY + 0.03, 0.7));

        // --- Монитор: корпус с фаской, «горб» с электроникой сзади, экран-стекло ---
        g.Children.Add(Model(Mesh3D.Slab(16, 9.2, 0.62, 0.34, 0.1), body));
        g.Children.Add(Model(Mesh3D.Slab(10.6, 6.2, 0.9, 1.4, 0.25), body, new TranslateTransform3D(0, -0.5, -0.72)));
        var screen = new MaterialGroup
        {
            Children =
            {
                new DiffuseMaterial(Brushes.Black),
                _screenLight,
                new SpecularMaterial(new SolidColorBrush(Color.FromArgb(0x48, 0xFF, 0xFF, 0xFF)), 90) // стекло ловит блики
            }
        };
        g.Children.Add(Model(Mesh3D.Grid(new Point3D(-7.72, -4.22, 0.34), new Vector3D(15.44, 0, 0), new Vector3D(0, 8.66, 0), 16, 9), screen));

        // Ножка (чуть наклонена) и подставка
        g.Children.Add(Model(Mesh3D.Slab(1.7, 3.8, 0.7, 0.34, 0.12), stand,
            Combine(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), -5)), new TranslateTransform3D(0, -6.05, -1.12))));
        g.Children.Add(Model(Mesh3D.Slab(7.2, 4.2, 0.34, 1.7, 0.12), stand,
            Combine(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), -90)), new TranslateTransform3D(0, DeskY + 0.17, -0.95))));

        // --- Screen Light Bar Pro: обтекаемый корпус, крепление и противовес за монитором ---
        var barProfile = Mesh3D.RoundedRect(0.8, 1.16, 0.36, 8);
        g.Children.Add(Model(Mesh3D.ExtrudeX(barProfile, -BarHalf, BarHalf), alu, new TranslateTransform3D(0, BarY, BarZ)));
        g.Children.Add(Model(Mesh3D.Slab(2.2, 0.36, 0.95, 0.15, 0.05), alu, new TranslateTransform3D(0, BarY - 0.05, BarZ - 0.85)));
        g.Children.Add(Model(Mesh3D.Slab(2.6, 1.7, 0.4, 0.3, 0.08), alu, new TranslateTransform3D(0, BarY - 0.95, -0.82)));

        // Фронтальный LED — на нижней передней скруглённой грани, светит вниз и вперёд
        var ledNormal = new Vector3D(0, -0.7, 0.71);
        double ly0 = BarY - 0.4, lz0 = BarZ + 0.3, ly1 = BarY - 0.2, lz1 = BarZ + 0.585;
        var off = ledNormal * 0.012;
        g.Children.Add(Glow(Mesh3D.Quad(
            new Point3D(-LedHalf, ly0, lz0) + off, new Point3D(LedHalf, ly0, lz0) + off,
            new Point3D(LedHalf, ly1, lz1) + off, new Point3D(-LedHalf, ly1, lz1) + off, ledNormal), _frontLed, emissiveOnly: true));

        // Задняя RGB-полоса: левая и правая половины — свои цвета
        var bz = BarZ - 0.586;
        g.Children.Add(Glow(Mesh3D.Quad(new Point3D(-LedHalf, BarY - 0.2, bz), new Point3D(LedHalf, BarY - 0.2, bz),
            new Point3D(LedHalf, BarY + 0.2, bz), new Point3D(-LedHalf, BarY + 0.2, bz), new Vector3D(0, 0, -1)), _backLed, emissiveOnly: true));

        // --- Пульт-«шайба» со светящимся кольцом ---
        g.Children.Add(Model(Mesh3D.Cylinder(new Point3D(PuckX, DeskY, PuckZ), PuckR, PuckH), puck));
        g.Children.Add(Model(Mesh3D.Cylinder(new Point3D(PuckX, DeskY + PuckH, PuckZ), PuckR * 0.62, 0.04), alu));
        g.Children.Add(Glow(Mesh3D.Ring(new Point3D(PuckX, DeskY + PuckH + 0.012, PuckZ), PuckR * 0.7, PuckR * 0.9), _ring, emissiveOnly: true));

        AddDeskProps(g);

        // --- Луч от LED к столу: едва заметная «объёмность» света. Последним — он прозрачный ---
        g.Children.Add(Glow(Mesh3D.Quad(
            new Point3D(-12, DeskY + 0.05, 11), new Point3D(12, DeskY + 0.05, 11),
            new Point3D(LedHalf, ly0 - 0.02, lz0 + 0.02), new Point3D(-LedHalf, ly0 - 0.02, lz0 + 0.02),
            new Vector3D(0, 0.55, 0.83)), _beam, twoSided: true));

        return g;
    }

    private static MeshGeometry3D WallQuad(double x0, double x1, double z)
        => Mesh3D.Quad(new Point3D(x0, -6, z), new Point3D(x1, -6, z), new Point3D(x1, 22, z), new Point3D(x0, 22, z), new Vector3D(0, 0, 1));

    /// <summary>Рабочее место: коврик, клавиатура, мышь, кружка, растение и постер на стене.</summary>
    private static void AddDeskProps(Model3DGroup g)
    {
        var dark = Matte(Color.FromRgb(0x1B, 0x1E, 0x24), 0x40, 30);
        var mat = Matte(Color.FromRgb(0x11, 0x13, 0x17), 0x10, 10);
        var mouse = Matte(Color.FromRgb(0x1C, 0x1F, 0x25), 0x90, 80);
        var ceramic = Matte(Color.FromRgb(0x2A, 0x2D, 0x33), 0x70, 60);
        var soil = Matte(Color.FromRgb(0x1A, 0x14, 0x10), 0, 1);
        var coffee = Matte(Color.FromRgb(0x2B, 0x1A, 0x10), 0x60, 90);
        var leafA = Matte(Color.FromRgb(0x1F, 0x6B, 0x45), 0x40, 30);
        var leafB = Matte(Color.FromRgb(0x2C, 0x8A, 0x57), 0x40, 30);
        var frame = Matte(Color.FromRgb(0x16, 0x18, 0x1C), 0x30, 30);
        var flat = new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), -90)); // плита «ложится» на стол

        // Коврик
        g.Children.Add(Model(Mesh3D.Slab(30, 13, 0.08, 0.8, 0.02), mat, Combine(flat, new TranslateTransform3D(0.5, DeskY + 0.04, 5.8))));
        g.Children.Add(Shadow(new Rect(-9.6, 3, 19.2, 6.4), MatY + 0.005, 0.55));

        // Клавиатура: раскладка — текстура на верхней грани, передний край чуть ниже
        var keys = new MaterialGroup { Children = { new DiffuseMaterial(KeyboardTexture()), new SpecularMaterial(new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)), 30) } };
        g.Children.Add(Model(Mesh3D.Slab(16.5, 5.4, 0.42, 0.45, 0.12), keys, Combine(flat,
            new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), 4)),
            new TranslateTransform3D(0, MatY + 0.24, 6))));

        // Мышь
        g.Children.Add(Shadow(new Rect(10.2, 4.7, 2.8, 3.4), MatY + 0.006, 0.6));
        g.Children.Add(Model(Mesh3D.Ellipsoid(new Point3D(11.6, MatY + 0.36, 6.4), 0.95, 0.4, 1.45, 18, 10), mouse));

        // Кружка с кофе и ручкой
        const double mugX = -10.9, mugZ = 4.4;
        g.Children.Add(Shadow(new Rect(mugX - 1.9, mugZ - 1.9, 3.8, 3.8), DeskY + 0.03, 0.7));
        g.Children.Add(Model(Mesh3D.Cylinder(new Point3D(mugX, DeskY, mugZ), 1.05, 2.2), ceramic));
        g.Children.Add(Model(Mesh3D.Cylinder(new Point3D(mugX, DeskY + 2.2, mugZ), 0.9, 0.015), coffee));
        g.Children.Add(Model(Mesh3D.Torus(new Point3D(mugX - 1.25, DeskY + 1.15, mugZ), 0.55, 0.13, 16, 8), ceramic));

        // Растение: горшок с ободком, земля и листья по «золотому углу», как у настоящих побегов
        const double potX = -13.8, potZ = -3.6, potTop = DeskY + 2.8;
        g.Children.Add(Shadow(new Rect(potX - 2.8, potZ - 2.8, 5.6, 5.6), DeskY + 0.03, 0.7));
        g.Children.Add(Model(Mesh3D.Cylinder(new Point3D(potX, DeskY, potZ), 1.6, 2.8), ceramic));
        g.Children.Add(Model(Mesh3D.Cylinder(new Point3D(potX, potTop - 0.3, potZ), 1.8, 0.3), ceramic));
        g.Children.Add(Model(Mesh3D.Cylinder(new Point3D(potX, potTop, potZ), 1.55, 0.02), soil));
        for (var i = 0; i < 13; i++)
        {
            var length = 2.1 + (i % 4) * 0.45;
            var leaf = Mesh3D.Ellipsoid(new Point3D(0, length, 0), 0.55, length, 0.1, 10, 6);
            var tilt = 18 + (i * 23) % 42;
            g.Children.Add(Model(leaf, i % 2 == 0 ? leafA : leafB, Combine(
                new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 0, 1), tilt)),
                new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), i * 137.5)),
                new TranslateTransform3D(potX, potTop, potZ))));
        }

        // Постер в рамке на стене справа — его подсвечивает правая половина подсветки
        const double posterX = 20.5, posterY = 3.6;
        g.Children.Add(Model(Mesh3D.Slab(7.2, 9.6, 0.36, 0.15, 0.05), frame, new TranslateTransform3D(posterX, posterY, WallZ + 0.2)));
        var poster = new MaterialGroup { Children = { new DiffuseMaterial(PosterTexture()), new SpecularMaterial(new SolidColorBrush(Color.FromArgb(0x20, 255, 255, 255)), 40) } };
        g.Children.Add(Model(Mesh3D.Quad(new Point3D(posterX - 3.1, posterY - 4.3, WallZ + 0.39), new Point3D(posterX + 3.1, posterY - 4.3, WallZ + 0.39),
            new Point3D(posterX + 3.1, posterY + 4.3, WallZ + 0.39), new Point3D(posterX - 3.1, posterY + 4.3, WallZ + 0.39), new Vector3D(0, 0, 1)), poster));
    }

    /// <summary>Раскладка клавиатуры: ряды клавиш, длинные модификаторы и пробел.</summary>
    private static Brush KeyboardTexture()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x14, 0x16, 0x1A)), null, new RectangleGeometry(new Rect(0, 0, 165, 54))));
        var key = new SolidColorBrush(Color.FromRgb(0x2B, 0x2F, 0x37));
        var mod = new SolidColorBrush(Color.FromRgb(0x24, 0x28, 0x2F));
        // Ширины клавиш в «единицах» по рядам: F-ряд, цифры, QWERTY, ASDF, ZXCV, нижний ряд
        double[][] rows =
        [
            [1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1],
            [1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2],
            [1.5, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1.5],
            [1.75, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2.25],
            [2.25, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2.75],
            [1.25, 1.25, 1.25, 6.25, 1.25, 1.25, 1.25, 1.25]
        ];
        const double unit = 10.6, gap = 1.1, top = 3;
        for (var r = 0; r < rows.Length; r++)
        {
            var x = 3.0;
            var y = top + r * 8.2 + (r > 0 ? 1.5 : 0);
            foreach (var w in rows[r])
            {
                var width = w * unit - gap;
                var brush = w > 1.1 || r == 0 ? mod : key;
                group.Children.Add(new GeometryDrawing(brush, null, new RectangleGeometry(new Rect(x, y, width, 6.6), 1.2, 1.2)));
                x += w * unit;
            }
        }
        group.Freeze();
        var result = new DrawingBrush(group) { Stretch = Stretch.Fill, Viewbox = new Rect(0, 0, 165, 54), ViewboxUnits = BrushMappingMode.Absolute };
        result.Freeze();
        return result;
    }

    /// <summary>Постер: крупная подпись и тонкие линии — как типографский принт.</summary>
    private static Brush PosterTexture()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x10, 0x12, 0x16)), null, new RectangleGeometry(new Rect(0, 0, 62, 86))));
        var ink = new SolidColorBrush(Color.FromRgb(0x5E, 0x66, 0x72));
        var face = new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var lines = new[] { "STAY", "IN THE", "LIGHT" };
        for (var i = 0; i < lines.Length; i++)
        {
            var text = new FormattedText(lines[i], CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 10, ink, 1.0);
            group.Children.Add(new GeometryDrawing(ink, null, text.BuildGeometry(new Point(8, 18 + i * 14))));
        }
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x2E, 0x34, 0x3D)), 0.6);
        group.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new Point(8, 66), new Point(54, 66))));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x1F, 0xC9, 0xF3)), null, new RectangleGeometry(new Rect(8, 70, 10, 1.2))));
        group.Freeze();
        var result = new DrawingBrush(group) { Stretch = Stretch.Fill, Viewbox = new Rect(0, 0, 62, 86), ViewboxUnits = BrushMappingMode.Absolute };
        result.Freeze();
        return result;
    }

    /// <summary>
    /// Картинка на экране монитора, пока нет синхронизации: редактор кода — дерево файлов,
    /// номера строк и «подсвеченные» строки кода. Поверх — лёгкий отсвет цветов подсветки.
    /// </summary>
    private DrawingBrush BuildWallpaper()
    {
        var group = new DrawingGroup();
        void Rect(double x, double y, double w, double h, Color c, double r = 0)
            => group.Children.Add(new GeometryDrawing(new SolidColorBrush(c), null, new RectangleGeometry(new System.Windows.Rect(x, y, w, h), r, r)));

        Rect(0, 0, 160, 90, Color.FromRgb(0x0A, 0x0E, 0x15));
        Rect(0, 0, 160, 5, Color.FromRgb(0x11, 0x16, 0x20));                 // заголовок окна
        foreach (var (x, c) in new[] { (4.0, Color.FromRgb(0xE0, 0x5A, 0x50)), (7.5, Color.FromRgb(0xE0, 0xB0, 0x40)), (11.0, Color.FromRgb(0x4C, 0xC0, 0x60)) })
            group.Children.Add(new GeometryDrawing(new SolidColorBrush(c), null, new EllipseGeometry(new Point(x, 2.5), 1.1, 1.1)));
        Rect(0, 5, 32, 81, Color.FromRgb(0x0D, 0x12, 0x1A));                 // дерево файлов
        Rect(32, 5, 42, 4, Color.FromRgb(0x13, 0x19, 0x24));                 // вкладка файла
        Rect(0, 86, 160, 4, Color.FromRgb(0x0E, 0x2A, 0x40));                // строка состояния

        var tree = Color.FromRgb(0x3A, 0x45, 0x55);
        var random = new Random(3);
        for (var i = 0; i < 14; i++)
        {
            var indent = i is 0 or 6 or 9 ? 0 : 3;
            var color = i == 4 ? Color.FromRgb(0x1F, 0xC9, 0xF3) : tree;
            if (i == 4) Rect(1, 9.6 + i * 5, 30, 4, Color.FromRgb(0x14, 0x22, 0x30));
            Rect(4 + indent, 11 + i * 5, 8 + random.Next(10), 1.6, color, 0.6);
        }

        Color[] syntax =
        [
            Color.FromRgb(0x56, 0x9C, 0xD6), Color.FromRgb(0x9C, 0xDC, 0xFE), Color.FromRgb(0xCE, 0x91, 0x78),
            Color.FromRgb(0x4E, 0xC9, 0xB0), Color.FromRgb(0xC5, 0x86, 0xC0), Color.FromRgb(0xDC, 0xDC, 0xAA)
        ];
        for (var line = 0; line < 15; line++)
        {
            var y = 12 + line * 4.8;
            Rect(35, y, 3, 1.5, Color.FromRgb(0x2A, 0x32, 0x40), 0.5);       // номер строки
            if (line is 3 or 8 or 12) continue;                              // пустые строки
            var x = 42.0 + (line is > 3 and < 8 or > 8 and < 12 ? 6 : 0);    // отступ внутри блоков
            var tokens = 2 + random.Next(4);
            for (var t = 0; t < tokens && x < 150; t++)
            {
                var w = 4 + random.Next(16);
                Rect(x, y, w, 1.6, syntax[random.Next(syntax.Length)], 0.6);
                x += w + 2.5;
            }
        }

        group.Children.Add(new GeometryDrawing(_wallpaperLeft, null, new EllipseGeometry(new Point(46, 40), 80, 60)));
        group.Children.Add(new GeometryDrawing(_wallpaperRight, null, new EllipseGeometry(new Point(118, 52), 76, 58)));
        return new DrawingBrush(group) { Stretch = Stretch.Fill, Viewbox = new System.Windows.Rect(0, 0, 160, 90), ViewboxUnits = BrushMappingMode.Absolute };
    }

    // ================= Вспомогательное =================

    private static GeometryModel3D Model(MeshGeometry3D mesh, Material material, Transform3D? transform = null)
        => new(mesh, material) { Transform = transform ?? Transform3D.Identity };

    /// <summary>Светящаяся или прозрачная поверхность: только эмиссия, без реакции на свет.</summary>
    private static GeometryModel3D Glow(MeshGeometry3D mesh, Brush brush, bool emissiveOnly = false, bool twoSided = false)
    {
        Material material = emissiveOnly
            ? new MaterialGroup { Children = { new DiffuseMaterial(Brushes.Black), new EmissiveMaterial(brush) } }
            : new EmissiveMaterial(brush);
        var model = new GeometryModel3D(mesh, material);
        if (twoSided) model.BackMaterial = material;
        return model;
    }

    /// <summary>Мягкая тень-«декаль» на столе.</summary>
    private static GeometryModel3D Shadow(Rect area, double y, double strength)
    {
        var brush = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb((byte)(255 * strength), 0, 0, 0), 0),
                new GradientStop(Color.FromArgb((byte)(120 * strength), 0, 0, 0), 0.55),
                new GradientStop(Color.FromArgb(0, 0, 0, 0), 1)
            }
        };
        brush.Freeze();
        var mesh = Mesh3D.Quad(new Point3D(area.Left, y, area.Bottom), new Point3D(area.Right, y, area.Bottom),
            new Point3D(area.Right, y, area.Top), new Point3D(area.Left, y, area.Top), new Vector3D(0, 1, 0));
        return new GeometryModel3D(mesh, new DiffuseMaterial(brush));
    }

    private static Material Matte(Color diffuse, byte specular, double power)
    {
        var group = new MaterialGroup { Children = { new DiffuseMaterial(new SolidColorBrush(diffuse)) } };
        if (specular > 0)
            group.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromArgb(specular, 255, 255, 255)), power));
        group.Freeze();
        return group;
    }

    private static Transform3D Combine(params Transform3D[] transforms)
    {
        var group = new Transform3DGroup();
        foreach (var t in transforms) group.Children.Add(t);
        return group;
    }

    /// <summary>Вертикальный градиент «прозрачно → цвет → прозрачно»: мягкая полоса света без размытия.</summary>
    private static LinearGradientBrush Glow3() => new()
    {
        StartPoint = new Point(0.5, 0),
        EndPoint = new Point(0.5, 1),
        GradientStops =
        {
            new GradientStop(Colors.Transparent, 0),
            new GradientStop(Colors.Transparent, 0.5),
            new GradientStop(Colors.Transparent, 1)
        }
    };

    private static void SetGlow3(LinearGradientBrush brush, Color c, double alpha)
    {
        var a = (byte)Math.Clamp(alpha, 0, 255);
        brush.GradientStops[0].Color = Color.FromArgb(0, c.R, c.G, c.B);
        brush.GradientStops[1].Color = Color.FromArgb(a, c.R, c.G, c.B);
        brush.GradientStops[2].Color = Color.FromArgb(0, c.R, c.G, c.B);
    }

    private static LinearGradientBrush Horizontal() => new()
    {
        StartPoint = new Point(0, 0.5),
        EndPoint = new Point(1, 0.5),
        GradientStops = { new GradientStop(Colors.Black, 0.2), new GradientStop(Colors.Black, 0.8) }
    };

    private static RadialGradientBrush Radial() => new()
    {
        GradientStops =
        {
            new GradientStop(Colors.Transparent, 0),
            new GradientStop(Colors.Transparent, 0.4),
            new GradientStop(Colors.Transparent, 1)
        }
    };

    /// <summary>Мягкое пятно: насыщенный центр, плавный спад к краю.</summary>
    private static void SetRadial(RadialGradientBrush brush, Color c, double alpha)
    {
        var a = Math.Clamp(alpha, 0, 1);
        brush.GradientStops[0].Color = Color.FromArgb((byte)(a * 255), c.R, c.G, c.B);
        brush.GradientStops[1].Color = Color.FromArgb((byte)(a * 0.42 * 255), c.R, c.G, c.B);
        brush.GradientStops[2].Color = Color.FromArgb(0, c.R, c.G, c.B);
    }

    /// <summary>Светящийся источник светлее своего цвета: тёплый LED выглядит почти белым с оттенком.</summary>
    private static Color Lift(Color c) => Mix(c, Colors.White, 0.35);

    private static Color Mix(Color from, Color to, double k)
    {
        k = Math.Clamp(k, 0, 1);
        return Color.FromRgb(
            (byte)(from.R + (to.R - from.R) * k),
            (byte)(from.G + (to.G - from.G) * k),
            (byte)(from.B + (to.B - from.B) * k));
    }

    private static Color Scale(Color c, double k)
    {
        k = Math.Clamp(k, 0, 1);
        return Color.FromRgb((byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k));
    }

    private static double EaseOutCubic(double x) => 1 - Math.Pow(1 - x, 3);

    /// <summary>Пылинка: медленно всплывает в свете подсветки, покачиваясь, и гаснет у краёв пути.</summary>
    private sealed class Mote
    {
        private readonly double _x, _phase, _speed, _sway;
        private readonly SolidColorBrush _brush = new(Colors.Transparent);

        public Mote(Random r)
        {
            _x = r.NextDouble() * 2 - 1;
            _phase = r.NextDouble();
            _speed = 0.025 + r.NextDouble() * 0.035;
            _sway = 4 + r.NextDouble() * 10;
            var size = 1.2 + r.NextDouble() * 2.2;
            Shape = new Ellipse { Width = size, Height = size, Fill = _brush };
        }

        public Ellipse Shape { get; }

        public void Update(double t, Point cloud, double width, Color left, Color right, double level)
        {
            var life = (t * _speed + _phase) % 1;                  // 0 → 1 за ~20–40 с
            var x = cloud.X + _x * width * 0.34 + Math.Sin(t * 0.6 + _phase * 9) * _sway;
            var y = cloud.Y + 70 - life * 150;
            Canvas.SetLeft(Shape, x);
            Canvas.SetTop(Shape, y);
            var fade = Math.Sin(life * Math.PI);                    // появляется и исчезает плавно
            var c = _x < 0 ? left : right;
            var lit = Color.FromRgb((byte)((c.R + 255) / 2), (byte)((c.G + 255) / 2), (byte)((c.B + 255) / 2));
            _brush.Color = Color.FromArgb((byte)(150 * fade * level), lit.R, lit.G, lit.B);
        }
    }
}
