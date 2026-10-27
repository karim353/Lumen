using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Lumen.Core;

namespace Lumen.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private static readonly string[] StateProps =
        ["power", "bright", "ct", "bg_power", "bg_bright", "bg_lmode", "bg_rgb", "bg_hue", "bg_sat"];

    private readonly AppSettings _settings;
    private readonly Dispatcher _ui;

    private readonly Throttler _mainBrightThrottle = new(TimeSpan.FromMilliseconds(300));
    private readonly Throttler _ctThrottle = new(TimeSpan.FromMilliseconds(300));
    private readonly Throttler _bgBrightThrottle = new(TimeSpan.FromMilliseconds(300));
    private readonly Throttler _colorThrottle = new(TimeSpan.FromMilliseconds(300));
    private readonly Throttler _effectThrottle = new(TimeSpan.FromMilliseconds(400));

    // Слайдеры подсветки: по TCP — не чаще ~3 раз в секунду (лимит лампы), по UDP — ~25 раз
    private static readonly TimeSpan TcpSliderInterval = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan UdpSliderInterval = TimeSpan.FromMilliseconds(40);

    private YeelightClient? _client;
    private IReadOnlySet<string>? _support; // методы, которые лампа объявила в discovery
    private UdpRealtimeSession? _udp;       // постоянная UDP-сессия lamp15: слайдеры подсветки, зоны, синхронизация
    private IRealtimeChannel? _channel;
    private bool _channelOwned;             // канал синхронизации открыт только для неё (music mode) — закрыть вместе с ней
    private ScreenSyncEngine? _sync;
    private CancellationTokenSource? _reconnectCts;

    // Последний кадр синхронизации для превью: движок шлёт до 30 кадров/с, UI обновляем не чаще отрисовки
    private SyncFrame _pendingFrame;
    private int _frameScheduled;

    // UDP без подтверждений: последнее значение каждого слайдера повторяем один раз, когда движение закончилось
    private readonly Dictionary<string, object[]> _udpLatest = new();
    private readonly DispatcherTimer _udpConfirmTimer;

    // Пауза синхронизации на время блокировки/сна
    private bool _syncPausedBySystem;
    private long _lastBgSendTick;
    private bool _sessionLocked;

    // true, пока применяем состояние, пришедшее ОТ лампы, — чтобы не отправить его обратно
    private bool _applying;

    // Точки подмены для тестов: подключение к поддельной лампе и её «ответ на discovery»
    internal Func<string, YeelightClient> ClientFactory { get; set; } = host => new YeelightClient(host);
    internal Func<string, Task<YeelightDeviceInfo?>> Probe { get; set; } =
        host => YeelightDiscovery.ProbeAsync(host, TimeSpan.FromSeconds(1.5));

    public MainViewModel(AppSettings settings, Dispatcher ui)
    {
        _settings = settings;
        _ui = ui;

        foreach (var d in settings.Devices)
            Devices.Add(new DeviceItem(d.Host, d.Name, d.Model));

        Effects = Core.Effects.All.Select(p => new EffectItem(p)).ToList();

        _syncFps = settings.SyncFps;
        _syncSmoothing = settings.SyncSmoothing;
        _syncSaturation = settings.SyncSaturation;
        _syncFollowBrightness = settings.SyncFollowBrightness;
        _syncZones = settings.SyncZones;
        _syncZonesSwap = settings.SyncZonesSwap;
        _effectSpeed = settings.EffectSpeed;
        _startMinimized = settings.StartMinimized;
        _autostart = SafeAutostartState();

        DiscoverCommand = new AsyncCommand(_ => DiscoverAsync());
        ToggleAddPanelCommand = new RelayCommand(_ => IsAddPanelOpen = !IsAddPanelOpen);
        AddManualCommand = new AsyncCommand(_ => AddManualAsync());
        RemoveDeviceCommand = new RelayCommand(p => { if (p is DeviceItem d) RemoveDevice(d); });
        SetCtCommand = new RelayCommand(p =>
        {
            if (int.TryParse(p?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var k)) MainCt = k;
        });
        PickSwatchCommand = new RelayCommand(p =>
        {
            if (p is not SwatchItem s) return;
            Mode = BackMode.Color;
            SetColor(s.Hue, s.Sat);
        });
        StartEffectCommand = new RelayCommand(p => { if (p is EffectItem e) StartEffect(e); });
        StopEffectCommand = new RelayCommand(_ => StopEffect());
        ToggleSyncCommand = new AsyncCommand(_ => IsSyncing ? StopSyncAsync() : StartSyncAsync(),
            _ => IsConnected || IsSyncing);
        SelectMonitorCommand = new RelayCommand(p => { if (p is MonitorItem m) SelectedMonitor = m; });
        TestZonesCommand = new AsyncCommand(_ => TestZonesAsync(), _ => CanTestZones);
        OpenLogCommand = new RelayCommand(_ => OpenLog());

        _udpConfirmTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background,
            (_, _) => ConfirmUdpValues(), ui) { IsEnabled = false };

        foreach (var t in new[] { _mainBrightThrottle, _ctThrottle, _bgBrightThrottle, _colorThrottle, _effectThrottle })
            t.QuotaStateChanged += limited => OnUi(() => OnQuotaStateChanged(limited));

        UpdatePreview();
        RefreshMonitors();
        InitDashboard();
    }

    // ================= Устройства =================

    public ObservableCollection<DeviceItem> Devices { get; } = [];

    private DeviceItem? _selectedDevice;
    public DeviceItem? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (!SetField(ref _selectedDevice, value)) return;
            RaiseDeviceInfo();
            _settings.SelectedHost = value?.Host;
            _settings.Save();
            _ = ConnectAsync(value);
        }
    }

    private string _status = "Нет подключения";
    public string Status { get => _status; set => SetField(ref _status, value); }

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            if (!SetField(ref _isConnected, value)) return;
            OnPropertyChanged(nameof(ControlsEnabled));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>Карточки управления активны при связи с лампой; во время синхронизации — всегда (её идёт по UDP).</summary>
    public bool ControlsEnabled => IsConnected || IsSyncing;

    private bool _isDiscovering;
    public bool IsDiscovering { get => _isDiscovering; set => SetField(ref _isDiscovering, value); }

    private bool _isAddPanelOpen;
    public bool IsAddPanelOpen { get => _isAddPanelOpen; set => SetField(ref _isAddPanelOpen, value); }

    private string _manualIp = "";
    public string ManualIp { get => _manualIp; set => SetField(ref _manualIp, value); }

    public ICommand DiscoverCommand { get; }
    public ICommand ToggleAddPanelCommand { get; }
    public ICommand AddManualCommand { get; }
    public ICommand RemoveDeviceCommand { get; }

    // ================= Основной свет =================

    private bool _mainPower;
    public bool MainPower
    {
        get => _mainPower;
        set
        {
            if (!SetField(ref _mainPower, value) || _applying) return;
            _ = SendAsync("set_power", value ? "on" : "off", "smooth", 300);
        }
    }

    private double _mainBright = 50;
    public double MainBright
    {
        get => _mainBright;
        set
        {
            if (!SetField(ref _mainBright, value) || _applying) return;
            EnsureMainOn();
            var v = Clamp(value, 1, 100);
            _mainBrightThrottle.Run(() => SendLatestAsync("set_bright", v, "smooth", 200));
        }
    }

    private double _mainCt = 4000;
    public double MainCt
    {
        get => _mainCt;
        set
        {
            if (!SetField(ref _mainCt, value)) return;
            OnPropertyChanged(nameof(MainCtText));
            OnPropertyChanged(nameof(CtPreset));
            if (_applying) return;
            EnsureMainOn();
            var v = Clamp(value, 2700, 6500);
            _ctThrottle.Run(() => SendLatestAsync("set_ct_abx", v, "smooth", 200));
        }
    }

    public string MainCtText => $"{(int)Math.Round(MainCt / 50) * 50} K";

    /// <summary>Какой пресет температуры сейчас выбран ("2700", "4000"…), или "" — своя.</summary>
    public string CtPreset => new[] { 2700, 4000, 5000, 6500 }.FirstOrDefault(p => Math.Abs(p - MainCt) < 60) is var p and > 0
        ? p.ToString(CultureInfo.InvariantCulture) : "";

    public ICommand SetCtCommand { get; }

    // ================= Задняя подсветка =================

    private bool _bgPower;
    public bool BgPower
    {
        get => _bgPower;
        set
        {
            if (!SetField(ref _bgPower, value) || _applying) return;
            if (!value && IsSyncing)
            {
                _ = StopSyncAndTurnBgOffAsync();
                return;
            }
            _ = value ? TurnBgOnAsync() : SendBgAsync("bg_set_power", "off", "smooth", 300);
        }
    }

    private double _bgBright = 80;
    public double BgBright
    {
        get => _bgBright;
        set
        {
            if (!SetField(ref _bgBright, value) || _applying) return;
            var v = Clamp(value, 1, 100);
            if (_sync != null)
            {
                _sync.Brightness = v; // во время синхронизации яркостью управляет движок
                return;
            }
            EnsureBgOn();
            _bgBrightThrottle.Run(() => SendBgAsync("bg_set_bright", v, "smooth", BgTransition(200)));
        }
    }

    private double _bgHue = 30;
    public double BgHue
    {
        get => _bgHue;
        set { if (SetField(ref _bgHue, value)) OnColorChanged(); }
    }

    private double _bgSat = 100;
    public double BgSat
    {
        get => _bgSat;
        set { if (SetField(ref _bgSat, value)) OnColorChanged(); }
    }

    private SolidColorBrush _previewBrush = Brushes.White;
    public SolidColorBrush PreviewBrush { get => _previewBrush; private set => SetField(ref _previewBrush, value); }

    private Color _hueColor = Colors.Red;
    /// <summary>Чистый тон без насыщенности — для градиента слайдера насыщенности.</summary>
    public Color HueColor { get => _hueColor; private set => SetField(ref _hueColor, value); }

    private string _colorHex = "#FFFFFF";
    public string ColorHex { get => _colorHex; private set => SetField(ref _colorHex, value); }

    public IReadOnlyList<SwatchItem> Swatches { get; } =
    [
        new("Красный", 0, 100), new("Оранжевый", 22, 100), new("Янтарный", 40, 100),
        new("Зелёный", 130, 100), new("Бирюзовый", 175, 100), new("Синий", 225, 100),
        new("Фиолетовый", 270, 100), new("Розовый", 320, 100), new("Белый", 0, 0)
    ];

    public ICommand PickSwatchCommand { get; }

    private BackMode _mode = BackMode.Color;
    public BackMode Mode
    {
        get => _mode;
        set
        {
            var old = _mode;
            if (!SetField(ref _mode, value)) return;
            if (old == BackMode.Screen && IsSyncing) _ = StopSyncAsync();
            if (value == BackMode.Screen) RefreshMonitors();
            if (value == BackMode.Color && !_applying) SendCurrentColor();
        }
    }

    // ================= Эффекты =================

    public IReadOnlyList<EffectItem> Effects { get; }

    private EffectItem? _activeEffect;

    private double _effectSpeed = 1;
    public double EffectSpeed
    {
        get => _effectSpeed;
        set
        {
            if (!SetField(ref _effectSpeed, value)) return;
            OnPropertyChanged(nameof(EffectSpeedText));
            _settings.EffectSpeed = value;
            if (_activeEffect != null)
            {
                var e = _activeEffect;
                _effectThrottle.Run(() => RunEffectAsync(e));
            }
        }
    }

    public string EffectSpeedText => $"×{EffectSpeed.ToString("0.0", CultureInfo.InvariantCulture)}";

    public ICommand StartEffectCommand { get; }
    public ICommand StopEffectCommand { get; }

    // ================= Синхронизация с экраном =================

    private bool _isSyncing;
    public bool IsSyncing
    {
        get => _isSyncing;
        private set
        {
            if (!SetField(ref _isSyncing, value)) return;
            OnPropertyChanged(nameof(SyncButtonText));
            OnPropertyChanged(nameof(SyncStateText));
            OnPropertyChanged(nameof(SyncEnabled));
            OnPropertyChanged(nameof(ControlsEnabled));
            OnPropertyChanged(nameof(BgBrightLabel));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string SyncButtonText => IsSyncing ? "Остановить синхронизацию" : "Запустить синхронизацию";

    private int _syncFps;
    public int SyncFps
    {
        get => _syncFps;
        set
        {
            if (!SetField(ref _syncFps, value)) return;
            _settings.SyncFps = value;
            if (_sync != null) _sync.Fps = value;
        }
    }

    private double _syncSmoothing;
    public double SyncSmoothing
    {
        get => _syncSmoothing;
        set
        {
            if (!SetField(ref _syncSmoothing, value)) return;
            OnPropertyChanged(nameof(SyncSmoothingText));
            _settings.SyncSmoothing = value;
            if (_sync != null) _sync.Smoothing = value;
        }
    }

    public string SyncSmoothingText => $"{Math.Round(SyncSmoothing * 100)} %";

    private double _syncSaturation;
    public double SyncSaturation
    {
        get => _syncSaturation;
        set
        {
            if (!SetField(ref _syncSaturation, value)) return;
            OnPropertyChanged(nameof(SyncSaturationText));
            _settings.SyncSaturation = value;
            if (_sync != null) _sync.SaturationBoost = value;
        }
    }

    public string SyncSaturationText => $"×{SyncSaturation.ToString("0.0", CultureInfo.InvariantCulture)}";

    private bool _syncFollowBrightness;
    public bool SyncFollowBrightness
    {
        get => _syncFollowBrightness;
        set
        {
            if (!SetField(ref _syncFollowBrightness, value)) return;
            OnPropertyChanged(nameof(BgBrightLabel));
            _settings.SyncFollowBrightness = value;
            if (_sync != null) _sync.FollowBrightness = value;
        }
    }

    private bool _syncZones;
    /// <summary>Левая и правая половины подсветки — по своим половинам экрана.</summary>
    public bool SyncZones
    {
        get => _syncZones;
        set
        {
            if (!SetField(ref _syncZones, value)) return;
            OnPropertyChanged(nameof(ZonesActive));
            OnPropertyChanged(nameof(BgBrightLabel));
            _settings.SyncZones = value;
            if (_sync != null) _sync.Zones = value;
        }
    }

    private bool _syncZonesSwap;
    /// <summary>Лампа считает левой правую половину — меняем зоны местами.</summary>
    public bool SyncZonesSwap
    {
        get => _syncZonesSwap;
        set
        {
            if (!SetField(ref _syncZonesSwap, value)) return;
            _settings.SyncZonesSwap = value;
            _settings.Save();
            if (_sync != null) _sync.SwapZones = value;
        }
    }

    /// <summary>Лампа умеет set_segment_rgb (Screen Light Bar Pro).</summary>
    public bool CanUseZones => _support?.Contains("set_segment_rgb") == true;

    /// <summary>Проверка зон идёт по UDP и только вне синхронизации.</summary>
    public bool CanTestZones => CanUseZones && _udp != null && !IsSyncing && !_isTestingZones;

    private bool _isTestingZones;
    /// <summary>Идёт «Проверить зоны»: подсветка сейчас красная слева и синяя справа.</summary>
    public bool IsTestingZones => _isTestingZones;

    public ICommand TestZonesCommand { get; }

    /// <summary>
    /// Что сейчас светит задняя подсветка — для 3D-сцены и фона окна: кадры синхронизации,
    /// проверка зон или выбранный цвет.
    /// </summary>
    /// <summary>Последняя миниатюра экрана во время синхронизации (null — синхронизации нет).</summary>
    public FrameImage? SyncThumbnail => IsSyncing ? _syncThumbnail : null;

    /// <summary>Растёт с каждой новой миниатюрой — сцене не нужно сравнивать картинки.</summary>
    public int SyncThumbnailVersion => Volatile.Read(ref _syncThumbnailVersion);

    private FrameImage? _syncThumbnail;
    private int _syncThumbnailVersion;

    public Color BackLeftColor => IsSyncing || _isTestingZones ? SyncLeftColor : PreviewBrush.Color;
    public Color BackRightColor => IsSyncing || _isTestingZones ? SyncRightColor : PreviewBrush.Color;

    /// <summary>В зонах lamp15 не даёт менять яркость на лету — «повторять яркость» недоступно.</summary>
    public bool ZonesActive => SyncZones && CanUseZones;

    public string BgBrightLabel => IsSyncing && SyncFollowBrightness && !ZonesActive ? "Яркость · максимум" : "Яркость";

    public ObservableCollection<MonitorItem> Monitors { get; } = [];

    private MonitorItem? _selectedMonitor;
    public MonitorItem? SelectedMonitor
    {
        get => _selectedMonitor;
        set
        {
            var old = _selectedMonitor;
            if (!SetField(ref _selectedMonitor, value)) return;
            if (old != null) old.IsSelected = false;
            if (value == null) return;
            value.IsSelected = true;
            _settings.SyncMonitor = value.DeviceName;
            if (_sync != null) _sync.MonitorDeviceName = value.DeviceName;
        }
    }

    public ICommand SelectMonitorCommand { get; }

    // Цвет «выключенной» полоски превью — как Bg3 в теме
    private static readonly Color IdleBarColor = Color.FromRgb(0x2C, 0x2C, 0x2C);

    private Color _syncLeftColor = IdleBarColor;
    /// <summary>Что сейчас светит левая половина подсветки (в режиме одной зоны — вся).</summary>
    public Color SyncLeftColor { get => _syncLeftColor; private set => SetField(ref _syncLeftColor, value); }

    private Color _syncRightColor = IdleBarColor;
    public Color SyncRightColor { get => _syncRightColor; private set => SetField(ref _syncRightColor, value); }

    private string _syncInfo = "";
    /// <summary>Строка под превью: канал и текущая яркость.</summary>
    public string SyncInfo { get => _syncInfo; private set => SetField(ref _syncInfo, value); }

    public ICommand ToggleSyncCommand { get; }

    // ================= Настройки =================

    private bool _autostart;
    public bool Autostart
    {
        get => _autostart;
        set
        {
            if (!SetField(ref _autostart, value)) return;
            try { Core.Autostart.Set(value, Environment.ProcessPath ?? ""); }
            catch (Exception ex) { Status = "Автозапуск: " + ex.Message; }
        }
    }

    private bool _startMinimized;
    public bool StartMinimized
    {
        get => _startMinimized;
        set
        {
            if (!SetField(ref _startMinimized, value)) return;
            _settings.StartMinimized = value;
            _settings.Save();
        }
    }

    public ICommand OpenLogCommand { get; }

    public string Version { get; } = "Lumen " + (typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "");

    // ================= Жизненный цикл =================

    public async Task InitializeAsync()
    {
        var saved = Devices.FirstOrDefault(d => d.Host == _settings.SelectedHost) ?? Devices.FirstOrDefault();
        if (saved != null)
            SelectedDevice = saved;
        else
            await DiscoverAsync();
    }

    public async Task ShutdownAsync()
    {
        _settings.Save();
        // Флаг SyncWasRunning не трогаем: при следующем запуске синхронизация продолжится
        await DisconnectAsync(forgetSync: false);
    }

    private async Task ConnectAsync(DeviceItem? device)
    {
        await DisconnectAsync(forgetSync: true);
        if (device == null)
        {
            Status = "Нет подключения";
            return;
        }

        var client = ClientFactory(device.Host);
        client.PropsChanged += p => OnUi(() => ApplyProps(p));
        client.ConnectionChanged += connected => OnUi(() => OnConnectionChanged(client, connected));
        _client = client;

        Status = $"Подключение к {device.Host}…";
        if (!await TryConnectAndRefreshAsync(client))
            StartReconnectLoop(client);
    }

    private async Task<bool> TryConnectAndRefreshAsync(YeelightClient client)
    {
        if (client != _client) return false; // устройство уже сменили
        try
        {
            await client.ConnectAsync();
            var props = await client.GetPropsAsync(StateProps);
            if (client != _client) return false;

            ApplyProps(props);
            IsConnected = true;
            OnConnected();
            Status = $"Подключено · {client.Host}";
            if (_support == null) _ = ProbeSupportAsync(client);
            else _ = ResumeSyncIfNeededAsync(); // переподключились после обрыва
            return true;
        }
        catch (Exception ex)
        {
            if (client == _client)
            {
                IsConnected = false;
                Status = ex.Message;
            }
            return false;
        }
    }

    /// <summary>Что умеет лампа (music mode или UDP, сегменты) — из ответа на discovery-запрос.</summary>
    private async Task ProbeSupportAsync(YeelightClient client)
    {
        YeelightDeviceInfo? info = null;
        try { info = await Probe(client.Host); }
        catch { /* не критично: синхронизация попробует оба канала */ }

        if (client != _client || info == null) return;
        _support = info.Support;
        OnDeviceInfo(info);
        OnPropertyChanged(nameof(CanUseZones));
        OnPropertyChanged(nameof(ZonesActive));
        OnPropertyChanged(nameof(BgBrightLabel));

        // Модель/имя могли быть неизвестны (лампу добавили по IP, а ответ не дошёл)
        if (SelectedDevice is { } d && d.Host == info.Host && (d.Model != info.Model || d.Name != info.Name))
            AddOrUpdateDevice(info.Host, info.Name, info.Model);

        if (_support.Contains("udp_sess_new")) await OpenControlUdpAsync(client);
        await ResumeSyncIfNeededAsync();
    }

    /// <summary>
    /// Постоянная UDP-сессия для команд подсветки: у неё нет лимита ~60 команд в минуту,
    /// поэтому слайдеры откликаются сразу. Основной свет по UDP не управляется — он остаётся на TCP.
    /// </summary>
    private async Task OpenControlUdpAsync(YeelightClient client)
    {
        if (_udp != null) return;
        try
        {
            var udp = await UdpRealtimeSession.StartAsync(client.Host, CanUseZones);
            if (client != _client)
            {
                await udp.DisposeAsync(); // пока открывали, сменили лампу
                return;
            }
            _udp = udp;
            _bgBrightThrottle.Interval = _colorThrottle.Interval = UdpSliderInterval;
            OnPropertyChanged(nameof(CanTestZones));
            OnPropertyChanged(nameof(ChannelText));
            CommandManager.InvalidateRequerySuggested();
        }
        catch (YeelightException ex)
        {
            CommandLog.Write("UDP для подсветки недоступен, слайдеры работают по TCP: " + ex.Message);
        }
    }

    private async Task CloseControlUdpAsync()
    {
        var udp = _udp;
        _udp = null;
        _bgBrightThrottle.Interval = _colorThrottle.Interval = TcpSliderInterval;
        _udpConfirmTimer.Stop();
        _udpLatest.Clear();
        OnPropertyChanged(nameof(CanTestZones));
        if (udp != null) await udp.DisposeAsync();
    }

    private void OnConnectionChanged(YeelightClient client, bool connected)
    {
        if (client != _client || connected) return;

        IsConnected = false;
        // Синхронизация идёт по своему каналу и от TCP-соединения не зависит — не останавливаем её
        Status = IsSyncing ? "Связь по TCP потеряна — синхронизация продолжается" : "Связь потеряна — переподключаюсь…";
        StartReconnectLoop(client);
    }

    private void StartReconnectLoop(YeelightClient client)
    {
        _reconnectCts?.Cancel();
        var cts = new CancellationTokenSource();
        _reconnectCts = cts;

        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(5), cts.Token); }
                catch (OperationCanceledException) { return; }

                var ok = await _ui.InvokeAsync(() => TryConnectAndRefreshAsync(client)).Task.Unwrap();
                if (ok) return;
            }
        });
    }

    private async Task DisconnectAsync(bool forgetSync)
    {
        _reconnectCts?.Cancel();
        _reconnectCts = null;

        await StopSyncAsync(keepStatus: false, forget: forgetSync);
        await CloseControlUdpAsync();

        var client = _client;
        _client = null;
        _support = null;
        OnPropertyChanged(nameof(CanUseZones));
        OnPropertyChanged(nameof(ZonesActive));
        if (client != null) await client.DisposeAsync();
        IsConnected = false;
        OnDisconnected();
    }

    // ================= Поиск и добавление =================

    private async Task DiscoverAsync()
    {
        IsDiscovering = true;
        Status = "Ищу лампы в сети…";
        try
        {
            var found = await YeelightDiscovery.DiscoverAsync(TimeSpan.FromSeconds(2.5));
            foreach (var info in found) AddOrUpdateDevice(info.Host, info.Name, info.Model);

            if (found.Count == 0)
                Status = "Лампы не найдены — добавь по IP";
            else if (SelectedDevice == null)
                SelectedDevice = Devices.First(d => d.Host == found[0].Host);
            else if (!IsConnected)
                Status = $"Найдено ламп: {found.Count}";
            else
                Status = $"Подключено · {SelectedDevice.Host} · найдено ламп: {found.Count}";
        }
        catch (Exception ex)
        {
            Status = "Ошибка поиска: " + ex.Message;
        }
        finally
        {
            IsDiscovering = false;
        }
    }

    private async Task AddManualAsync()
    {
        var host = ManualIp.Trim();
        if (!IPAddress.TryParse(host, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            Status = "Неверный IP-адрес";
            return;
        }

        // Пробуем узнать модель/имя, но добавляем в любом случае
        YeelightDeviceInfo? info = null;
        try { info = await YeelightDiscovery.ProbeAsync(host, TimeSpan.FromSeconds(1)); } catch { /* ignore */ }

        var item = AddOrUpdateDevice(host, info?.Name ?? "", info?.Model ?? "");
        ManualIp = "";
        IsAddPanelOpen = false;
        SelectedDevice = item;
    }

    private DeviceItem AddOrUpdateDevice(string host, string name, string model)
    {
        var existing = Devices.FirstOrDefault(d => d.Host == host);
        if (existing != null
            && (existing.Name == name || string.IsNullOrEmpty(name))
            && (existing.Model == model || string.IsNullOrEmpty(model)))
            return existing;
        if (existing != null)
        {
            if (string.IsNullOrEmpty(name)) name = existing.Name;
            if (string.IsNullOrEmpty(model)) model = existing.Model;
        }

        var item = new DeviceItem(host, name, model);
        if (existing != null)
        {
            var idx = Devices.IndexOf(existing);
            Devices[idx] = item;
            if (_selectedDevice == existing) _selectedDevice = item;
            OnPropertyChanged(nameof(SelectedDevice));
        }
        else
        {
            Devices.Add(item);
        }

        SaveDevices();
        return item;
    }

    private void RemoveDevice(DeviceItem device)
    {
        Devices.Remove(device);
        SaveDevices();
        if (SelectedDevice == device) SelectedDevice = Devices.FirstOrDefault();
    }

    private void SaveDevices()
    {
        _settings.Devices = Devices.Select(d => new SavedDevice { Host = d.Host, Name = d.Name, Model = d.Model }).ToList();
        _settings.Save();
    }

    // ================= Состояние от лампы =================

    private void ApplyProps(IReadOnlyDictionary<string, string> p)
    {
        _applying = true;
        try
        {
            if (p.TryGetValue("power", out var power)) MainPower = power == "on";
            if (TryInt(p, "bright", out var bright)) MainBright = bright;
            if (TryInt(p, "ct", out var ct) && ct > 0) MainCt = Clamp(ct, 2700, 6500);
            if (p.TryGetValue("bg_power", out var bgPower)) BgPower = bgPower == "on";

            // Во время синхронизации яркость и цвет меняются 20 раз в секунду — слайдеры не трогаем
            if (IsSyncing) return;

            // Только что сами двигали слайдер: «эхо» лампы приходит с отставанием и дёргало бы ползунок назад
            if (Environment.TickCount64 - Interlocked.Read(ref _lastBgSendTick) < 700) return;

            if (TryInt(p, "bg_bright", out var bgBright)) BgBright = bgBright;

            p.TryGetValue("bg_lmode", out var lmode);
            if (lmode == "3" && TryInt(p, "bg_hue", out var hue) && TryInt(p, "bg_sat", out var sat))
            {
                SetColorSilently(hue, sat);
            }
            // rgb == CurrentRgb — это лампа подтверждает наш же цвет: пересчёт RGB→HSV дал бы дрожание слайдера
            else if (TryInt(p, "bg_rgb", out var rgb) && rgb > 0 && lmode != "2" && rgb != CurrentRgb())
            {
                var (r, g, b) = ColorMath.FromYeelightRgb(rgb);
                var (h, s, _) = ColorMath.RgbToHsv(r, g, b);
                SetColorSilently(h, s * 100);
            }
        }
        finally
        {
            _applying = false;
        }
    }

    // ================= Цвет =================

    private void SetColor(double hue, double sat)
    {
        _applying = true;
        BgHue = hue;
        BgSat = sat;
        _applying = false;
        UpdatePreview();
        SendCurrentColor();
    }

    private void SetColorSilently(double hue, double sat)
    {
        _bgHue = hue;
        _bgSat = sat;
        OnPropertyChanged(nameof(BgHue));
        OnPropertyChanged(nameof(BgSat));
        UpdatePreview();
    }

    private void OnColorChanged()
    {
        UpdatePreview();
        if (_applying) return;
        if (Mode != BackMode.Color) Mode = BackMode.Color;
        else SendCurrentColor();
    }

    private void SendCurrentColor()
    {
        if (IsSyncing) return;
        ClearActiveEffect();
        EnsureBgOn();
        // RGB, а не bg_set_hsv: в режиме HSV lamp15 после выключения не включается командой bg_set_power
        var rgb = CurrentRgb();
        _colorThrottle.Run(() => SendBgAsync("bg_set_rgb", rgb, "smooth", BgTransition(250)));
    }

    private void UpdatePreview()
    {
        var (r, g, b) = ColorMath.HsvToRgb(BgHue, BgSat / 100.0, 1);
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        PreviewBrush = brush;
        ColorHex = $"#{r:X2}{g:X2}{b:X2}";

        var (hr, hg, hb) = ColorMath.HsvToRgb(BgHue, 1, 1);
        HueColor = Color.FromRgb(hr, hg, hb);
    }

    private int CurrentRgb()
    {
        var (r, g, b) = ColorMath.HsvToRgb(BgHue, BgSat / 100.0, 1);
        return ColorMath.ToYeelightRgb(r, g, b);
    }

    // ================= Эффекты =================

    private void StartEffect(EffectItem effect)
    {
        if (IsSyncing) _ = StopSyncAsync();
        _applying = true;
        Mode = BackMode.Effect;
        _applying = false;

        ClearActiveEffect();
        effect.IsActive = true;
        _activeEffect = effect;
        OnPropertyChanged(nameof(ActiveEffectName));

        EnsureBgOn();
        _effectThrottle.Run(() => RunEffectAsync(effect));
    }

    private Task RunEffectAsync(EffectItem effect)
    {
        var flow = effect.Preset.BuildFlow(CurrentRgb(), EffectSpeed);
        return SendAsync("bg_start_cf", 0, 1, flow); // 0 = бесконечно, 1 = остаться на последнем цвете
    }

    private void StopEffect()
    {
        if (_activeEffect == null) return;
        ClearActiveEffect();
        _ = SendAsync("bg_stop_cf");
    }

    private void ClearActiveEffect()
    {
        if (_activeEffect != null) _activeEffect.IsActive = false;
        _activeEffect = null;
        OnPropertyChanged(nameof(ActiveEffectName));
    }

    // ================= Синхронизация =================

    private async Task StartSyncAsync()
    {
        var client = _client;
        if (client == null || IsSyncing) return;

        ClearActiveEffect();
        _applying = true;
        Mode = BackMode.Screen;
        _applying = false;
        RefreshMonitors();

        Status = "Открываю канал синхронизации…";
        try
        {
            // Включаем подсветку по TCP: по UDP bg_set_power из «застрявшего» состояния lamp15 не сработает
            await TurnBgOnAsync();

            // Для lamp15 уже есть UDP-сессия слайдеров — синхронизация идёт через неё же
            // (сессий у лампы не больше четырёх). Для остальных ламп открываем music mode.
            var shared = _udp is { IsAlive: true } ? _udp : null;
            var channel = shared ?? await RealtimeChannel.OpenAsync(client, _support);
            if (client != _client)
            {
                if (shared == null) await channel.DisposeAsync(); // пока открывали, сменили лампу
                return;
            }

            var sync = new ScreenSyncEngine(channel)
            {
                Fps = SyncFps,
                Smoothing = SyncSmoothing,
                SaturationBoost = SyncSaturation,
                FollowBrightness = SyncFollowBrightness,
                Brightness = Clamp(BgBright, 1, 100),
                Zones = SyncZones,
                SwapZones = SyncZonesSwap,
                MonitorDeviceName = SelectedMonitor?.DeviceName
            };
            sync.Faulted += ex => OnUi(() =>
            {
                if (_sync != sync) return;
                Status = "Синхронизация остановлена: " + ex.Message;
                _ = StopSyncAsync(keepStatus: true);
            });
            sync.FrameSent += OnFrameSent;
            sync.Thumbnail += f =>
            {
                Volatile.Write(ref _syncThumbnail, f);
                Interlocked.Increment(ref _syncThumbnailVersion);
            };

            _channel = channel;
            _channelOwned = shared == null;
            _sync = sync;
            sync.Start();
            _udpConfirmTimer.Stop();
            _udpLatest.Clear();

            _applying = true;
            BgPower = true;
            _applying = false;

            IsSyncing = true;
            Status = $"Синхронизация · {channel.Name} · {client.Host}";
            OnPropertyChanged(nameof(CanTestZones));
            CommandLog.Write($"Синхронизация запущена: {channel.Name}, зоны={SyncZones}, монитор={SelectedMonitor?.DeviceName}");

            if (!_settings.SyncWasRunning)
            {
                _settings.SyncWasRunning = true;
                _settings.Save();
            }
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

    /// <summary>Остановка по желанию пользователя: при следующем запуске Lumen синхронизация не продолжится.</summary>
    public Task StopSyncAsync() => StopSyncAsync(keepStatus: false, forget: true);

    /// <param name="forget">false — остановка «не по воле пользователя» (выход, сбой, блокировка):
    /// синхронизация продолжится, когда будет можно.</param>
    private async Task StopSyncAsync(bool keepStatus, bool forget = false)
    {
        var sync = _sync;
        var channel = _channel;
        var owned = _channelOwned;
        _sync = null;
        _channel = null;
        IsSyncing = false;
        OnPropertyChanged(nameof(CanTestZones));

        if (sync != null)
        {
            CommandLog.Write("Синхронизация остановлена" + (forget ? "" : " (продолжится автоматически)"));
            if (forget && _settings.SyncWasRunning)
            {
                _settings.SyncWasRunning = false;
                _settings.Save();
            }
        }

        if (sync != null) await sync.DisposeAsync();
        if (channel != null && owned) await channel.DisposeAsync();

        SyncLeftColor = SyncRightColor = IdleBarColor;
        SyncInfo = "";

        if (!keepStatus && sync != null && IsConnected && _client != null)
            Status = $"Подключено · {_client.Host}";
    }

    /// <summary>
    /// Выключить подсветку во время синхронизации. После кадра set_segment_rgb lamp15 около секунды
    /// игнорирует команды питания, поэтому выключаем с паузой.
    /// </summary>
    private async Task StopSyncAndTurnBgOffAsync()
    {
        await StopSyncAsync();
        await Task.Delay(1200);
        if (!BgPower) await SendBgAsync("bg_set_power", "off", "smooth", 300);
    }

    /// <summary>Продолжить синхронизацию, если она шла при выходе из Lumen или до обрыва связи.</summary>
    private async Task ResumeSyncIfNeededAsync()
    {
        if (!_settings.SyncWasRunning || IsSyncing || _sessionLocked || _syncPausedBySystem || !IsConnected) return;
        CommandLog.Write("Продолжаю синхронизацию, начатую раньше");
        await StartSyncAsync();
    }

    // ================= Блокировка и сон =================

    /// <summary>Экран заблокирован или ПК засыпает — синхронизацию ставим на паузу.</summary>
    public void PauseForSystem(string reason, bool isLock)
    {
        CommandLog.Write("Система: " + reason);
        if (isLock) _sessionLocked = true;
        if (!IsSyncing) return;

        _syncPausedBySystem = true;
        _ = StopSyncAsync(keepStatus: true, forget: false);
        Status = "Синхронизация на паузе: " + reason;
    }

    /// <summary>Разблокировали или проснулись — продолжаем, если ставили на паузу.</summary>
    public async Task ResumeAfterSystemAsync(string reason, bool isUnlock)
    {
        CommandLog.Write("Система: " + reason);
        if (isUnlock) _sessionLocked = false;
        if (!_syncPausedBySystem || _sessionLocked) return; // после сна часто ещё экран блокировки

        _syncPausedBySystem = false;
        // После сна Wi-Fi и лампа поднимаются не сразу — ждём соединение до 30 секунд
        for (var i = 0; i < 60 && !IsConnected; i++) await Task.Delay(500);
        if (IsConnected && !IsSyncing) await StartSyncAsync();
        else if (!IsConnected) Status = "Синхронизация продолжится, когда лампа снова будет на связи";
    }

    // ================= Проверка зон =================

    /// <summary>
    /// 3 секунды красный слева и синий справа (с учётом «поменять местами»), потом прежний цвет.
    /// Так видно, совпадает ли левая половина подсветки с левой половиной экрана.
    /// </summary>
    private async Task TestZonesAsync()
    {
        var udp = _udp;
        if (udp == null || IsSyncing) return;

        _isTestingZones = true;
        OnPropertyChanged(nameof(CanTestZones));
        try
        {
            const int red = 0xFF0000, blue = 0x0000FF;
            SyncLeftColor = Color.FromRgb(255, 0, 0);
            SyncRightColor = Color.FromRgb(0, 0, 255);
            Status = "Проверка зон: слева должен гореть красный, справа — синий";
            CommandLog.Write($"Проверка зон, поменять местами={SyncZonesSwap}");

            EnsureBgOn();
            for (var i = 0; i < 6; i++) // повторяем: UDP-пакет может потеряться
            {
                if (SyncZonesSwap) await udp.SendAsync("set_segment_rgb", blue, red);
                else await udp.SendAsync("set_segment_rgb", red, blue);
                await Task.Delay(500);
            }

            await Task.Delay(1200); // после сегментов lamp15 ~1 с игнорирует остальные команды
            SyncLeftColor = SyncRightColor = IdleBarColor;
            Status = IsConnected && _client != null ? $"Подключено · {_client.Host}" : Status;
            await TurnBgOnAsync(); // сцена возвращает обычный цвет и яркость
        }
        finally
        {
            _isTestingZones = false;
            OnPropertyChanged(nameof(CanTestZones));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void OpenLog()
    {
        try
        {
            if (!System.IO.File.Exists(CommandLog.FilePath)) CommandLog.Write("Журнал открыт из настроек");
            CommandLog.Close(); // чтобы редактор открыл актуальное содержимое
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(CommandLog.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status = "Не удалось открыть журнал: " + ex.Message;
        }
    }

    private void OnFrameSent(SyncFrame frame)
    {
        _pendingFrame = frame;
        if (Interlocked.Exchange(ref _frameScheduled, 1) == 1) return;

        _ui.BeginInvoke(DispatcherPriority.Render, () =>
        {
            Interlocked.Exchange(ref _frameScheduled, 0);
            if (!IsSyncing) return;

            var f = _pendingFrame;
            SyncLeftColor = ToColor(f.LeftRgb);
            SyncRightColor = ToColor(f.RightRgb);
            var bright = f.Brightness > 0 ? $" · яркость {f.Brightness} %" : "";
            SyncInfo = $"{_channel?.Name} · {f.Capture} · {Math.Round(f.Fps)} к/с{bright}";
        });
    }

    private static Color ToColor(int rgb)
    {
        var (r, g, b) = ColorMath.FromYeelightRgb(rgb);
        return Color.FromRgb(r, g, b);
    }

    private void RefreshMonitors()
    {
        IReadOnlyList<DisplayMonitor> all;
        try { all = DisplayMonitors.GetAll(); }
        catch { return; }

        var wanted = SelectedMonitor?.DeviceName ?? _settings.SyncMonitor;
        Monitors.Clear();
        for (var i = 0; i < all.Count; i++) Monitors.Add(new MonitorItem(all[i], i + 1));

        _selectedMonitor = null;
        SelectedMonitor = Monitors.FirstOrDefault(m => m.DeviceName == wanted)
                          ?? Monitors.FirstOrDefault(m => m.IsPrimary)
                          ?? Monitors.FirstOrDefault();
        OnPropertyChanged(nameof(HasSeveralMonitors));
    }

    public bool HasSeveralMonitors => Monitors.Count > 1;

    // ================= Вспомогательное =================

    private void EnsureMainOn()
    {
        if (MainPower) return;
        _mainPower = true;
        OnPropertyChanged(nameof(MainPower));
        _ = SendAsync("set_power", "on", "smooth", 300);
    }

    private void EnsureBgOn()
    {
        if (BgPower) return;
        _bgPower = true;
        OnPropertyChanged(nameof(BgPower));
        _ = TurnBgOnAsync();
    }

    /// <summary>
    /// Включить подсветку. lamp15 (прошивка 38), если её выключили в режиме HSV, отвечает на
    /// bg_set_power on «ok», но не включается — помогало только переподключение питания.
    /// bg_set_scene включает из любого состояния и сразу задаёт цвет и яркость.
    /// </summary>
    private Task TurnBgOnAsync()
    {
        if (_activeEffect != null)
            return SendAsync("bg_set_scene", "cf", 0, 1, _activeEffect.Preset.BuildFlow(CurrentRgb(), EffectSpeed));
        return SendAsync("bg_set_scene", "color", CurrentRgb(), Clamp(BgBright, 1, 100));
    }

    private async Task SendAsync(string method, params object[] parameters)
    {
        var client = _client;
        if (client == null) return;
        try
        {
            await client.SendAsync(method, parameters).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Превышение лимита команд не критично — следующее значение уйдёт следом
            if (ex.Message.Contains("quota", StringComparison.OrdinalIgnoreCase)) return;
            OnUi(() => Status = ex.Message);
        }
    }

    /// <summary>
    /// Отправка значения со слайдера: ошибку лимита пробрасываем — <see cref="Throttler"/> повторит
    /// последнее значение, когда лампа «остынет».
    /// </summary>
    private async Task SendLatestAsync(string method, params object[] parameters)
    {
        var client = _client;
        if (client == null) return;
        try
        {
            await client.SendAsync(method, parameters).ConfigureAwait(false);
        }
        catch (YeelightQuotaException)
        {
            throw;
        }
        catch (Exception ex)
        {
            OnUi(() => Status = ex.Message);
        }
    }

    /// <summary>
    /// Команда подсветки: по UDP-сессии, если она есть (без лимита, без ответа), иначе по TCP.
    /// Ошибку лимита TCP пробрасываем — <see cref="Throttler"/> повторит последнее значение.
    /// </summary>
    private async Task SendBgAsync(string method, params object[] parameters)
    {
        Interlocked.Exchange(ref _lastBgSendTick, Environment.TickCount64);
        var udp = _udp;
        if (udp is { IsAlive: true } && !IsSyncing)
        {
            CommandLog.Write("UDP →", method, parameters);
            try
            {
                await udp.SendAsync(method, parameters).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                OnUi(() => Status = ex.Message);
                return;
            }
            OnUi(() => RememberUdpValue(method, parameters));
            return;
        }
        await SendLatestAsync(method, parameters).ConfigureAwait(false);
    }

    /// <summary>Плавный переход: по UDP команды идут часто — переход короче, иначе слайдер «отстаёт».</summary>
    private int BgTransition(int tcpMs) => _udp != null ? 60 : tcpMs;

    private void RememberUdpValue(string method, object[] parameters)
    {
        _udpLatest[method] = parameters;
        _udpConfirmTimer.Stop();
        _udpConfirmTimer.Start();
    }

    /// <summary>Движение слайдера закончилось — дублируем последние значения на случай потери пакета.</summary>
    private void ConfirmUdpValues()
    {
        _udpConfirmTimer.Stop();
        var udp = _udp;
        if (udp == null || IsSyncing) { _udpLatest.Clear(); return; }
        foreach (var (method, parameters) in _udpLatest)
            _ = udp.SendAsync(method, parameters);
        _udpLatest.Clear();
    }

    private const string QuotaStatus = "Лампа просит паузу (лимит ~60 команд в минуту) — значение применится через пару секунд";

    private void OnQuotaStateChanged(bool limited)
    {
        if (limited) Status = QuotaStatus;
        else if (Status == QuotaStatus && _client != null)
            Status = IsConnected ? $"Подключено · {_client.Host}" : "Нет подключения";
    }

    private void OnUi(Action action)
    {
        if (_ui.CheckAccess()) action();
        else _ui.BeginInvoke(action);
    }

    private static int Clamp(double v, int min, int max) => (int)Math.Round(Math.Clamp(v, min, max));

    private static bool TryInt(IReadOnlyDictionary<string, string> p, string key, out int value)
    {
        value = 0;
        return p.TryGetValue(key, out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool SafeAutostartState()
    {
        try { return Core.Autostart.IsEnabled; }
        catch { return false; }
    }
}
