using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Lumen.Core;
using Lumen.Views;

namespace Lumen.ViewModels;

public enum AppPage { Home, Devices, Effects, Sync, Schedule, Settings }

/// <summary>
/// Дашборд: разделы меню, сведения о лампе (модель, прошивка, время работы, переименование),
/// расписание и энергосбережение. Управление светом — в основном файле модели.
/// </summary>
public sealed partial class MainViewModel
{
    private DispatcherTimer? _clock;
    private DateTime _lastScheduleCheck;
    private YeelightDeviceInfo? _info;
    private DateTime? _connectedSince;

    private void InitDashboard()
    {
        _energySaving = _settings.EnergySaving;
        Motion.SetEnergySaving(_energySaving);

        NavigateCommand = new RelayCommand(p =>
        {
            if (Enum.TryParse<AppPage>(p?.ToString(), out var page)) CurrentPage = page;
        });
        SelectModeCommand = new RelayCommand(p =>
        {
            // На главной: «Цвет» — сразу режим цвета, «Эффекты» и «Экран» ведут на свои страницы
            switch (p?.ToString())
            {
                case "Color": Mode = BackMode.Color; break;
                case "Effect": CurrentPage = AppPage.Effects; break;
                case "Screen": CurrentPage = AppPage.Sync; break;
            }
        });
        CopyIpCommand = new RelayCommand(_ =>
        {
            if (SelectedDevice == null) return;
            try { Clipboard.SetText(SelectedDevice.Host); Status = $"IP {SelectedDevice.Host} скопирован"; }
            catch { /* буфер обмена занят другим приложением */ }
        });
        StartRenameCommand = new RelayCommand(_ =>
        {
            RenameText = SelectedDevice?.Name ?? "";
            IsRenaming = true;
        }, _ => IsConnected);
        SaveRenameCommand = new AsyncCommand(_ => SaveRenameAsync());
        CancelRenameCommand = new RelayCommand(_ => IsRenaming = false);

        AddRuleCommand = new RelayCommand(_ => AddRule());
        RemoveRuleCommand = new RelayCommand(p => { if (p is ScheduleItem item) RemoveRule(item); });

        foreach (var rule in _settings.Schedule) ScheduleItems.Add(new ScheduleItem(rule, SaveSchedule));
        RefreshUpcoming();

        _lastScheduleCheck = DateTime.Now;
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(10), DispatcherPriority.Background, (_, _) => OnClock(), _ui);
        _clock.Start();
    }

    // ================= Разделы =================

    private AppPage _currentPage = AppPage.Home;
    public AppPage CurrentPage
    {
        get => _currentPage;
        set
        {
            if (!SetField(ref _currentPage, value)) return;
            if (value == AppPage.Sync) RefreshMonitors();
        }
    }

    public ICommand NavigateCommand { get; private set; } = null!;
    public ICommand SelectModeCommand { get; private set; } = null!;

    // ================= Сведения о лампе =================

    public string ModelName => SelectedDevice?.ModelName ?? "—";
    public string ModelCode => _info?.Model ?? SelectedDevice?.Model ?? "—";
    public string FirmwareText => string.IsNullOrEmpty(_info?.FirmwareVersion) ? "—" : $"v{_info!.FirmwareVersion}";
    public string LampIdText => _info?.Id ?? "—";

    /// <summary>Сколько лампа на связи с Lumen (лампа сама время работы не сообщает).</summary>
    public string UptimeText
    {
        get
        {
            if (_connectedSince is not { } since || !IsConnected) return "—";
            var span = DateTime.Now - since;
            return span.TotalHours >= 1 ? $"{(int)span.TotalHours} ч {span.Minutes} мин" : $"{Math.Max(1, span.Minutes)} мин";
        }
    }

    /// <summary>Что лампа умеет — список методов из discovery, для страницы «Устройства».</summary>
    public string SupportText => _support == null ? "—" : string.Join("  ·  ", _support.OrderBy(s => s));

    public string ChannelText => _udp != null ? "TCP 55443 + UDP-сессия 55444" : IsConnected ? "TCP 55443" : "—";

    public ICommand CopyIpCommand { get; private set; } = null!;

    private void OnDeviceInfo(YeelightDeviceInfo info)
    {
        _info = info;
        RaiseDeviceInfo();
    }

    private void OnConnected()
    {
        _connectedSince ??= DateTime.Now;
        RaiseDeviceInfo();
    }

    private void OnDisconnected()
    {
        _info = null;
        _connectedSince = null;
        IsRenaming = false;
        RaiseDeviceInfo();
    }

    private void RaiseDeviceInfo()
    {
        foreach (var name in new[] { nameof(ModelName), nameof(ModelCode), nameof(FirmwareText), nameof(LampIdText),
                     nameof(UptimeText), nameof(SupportText), nameof(ChannelText) })
            OnPropertyChanged(name);
    }

    // ---------- Переименование: лампа хранит имя сама (set_name) ----------

    private bool _isRenaming;
    public bool IsRenaming { get => _isRenaming; set => SetField(ref _isRenaming, value); }

    private string _renameText = "";
    public string RenameText { get => _renameText; set => SetField(ref _renameText, value); }

    public ICommand StartRenameCommand { get; private set; } = null!;
    public ICommand SaveRenameCommand { get; private set; } = null!;
    public ICommand CancelRenameCommand { get; private set; } = null!;

    private async Task SaveRenameAsync()
    {
        var device = SelectedDevice;
        var name = RenameText.Trim();
        IsRenaming = false;
        if (device == null || name == device.Name) return;

        await SendAsync("set_name", name);
        AddOrUpdateDevice(device.Host, name, device.Model);
        // AddOrUpdateDevice не перезаписывает имя пустым — очищаем отдельно
        if (name.Length == 0 && SelectedDevice is { } d && d.Name.Length > 0)
        {
            var idx = Devices.IndexOf(d);
            Devices[idx] = _selectedDevice = new DeviceItem(d.Host, "", d.Model);
            OnPropertyChanged(nameof(SelectedDevice));
            SaveDevices();
        }
        RaiseDeviceInfo();
        CommandLog.Write($"Лампа переименована: «{name}»");
    }

    // ================= Состояние для правой панели =================

    public string ActiveEffectName => _activeEffect?.Name ?? "Статический";
    public string SyncStateText => IsSyncing ? "Идёт" : "Выключена";

    /// <summary>Переключатель «Синхронизация с экраном» в блоке «Система».</summary>
    public bool SyncEnabled
    {
        get => IsSyncing;
        set
        {
            if (value == IsSyncing) return;
            _ = value ? StartSyncAsync() : StopSyncAsync();
        }
    }

    // ================= Энергосбережение =================

    private bool _energySaving;
    /// <summary>Без декоративной анимации: сцена не «дышит», фон не дрейфует, превью стоят.</summary>
    public bool EnergySaving
    {
        get => _energySaving;
        set
        {
            if (!SetField(ref _energySaving, value)) return;
            Motion.SetEnergySaving(value);
            _settings.EnergySaving = value;
            _settings.Save();
        }
    }

    // ================= Расписание =================

    public ObservableCollection<ScheduleItem> ScheduleItems { get; } = [];
    public ObservableCollection<UpcomingItem> Upcoming { get; } = [];
    public ScheduleEditor NewRule { get; } = new();

    public bool HasSchedule => ScheduleItems.Count > 0;

    public ICommand AddRuleCommand { get; private set; } = null!;
    public ICommand RemoveRuleCommand { get; private set; } = null!;

    private void AddRule()
    {
        if (!NewRule.TryBuild(out var rule, out var error))
        {
            NewRule.Error = error;
            return;
        }
        NewRule.Error = "";
        ScheduleItems.Add(new ScheduleItem(rule, SaveSchedule));
        SaveSchedule();
        CommandLog.Write($"Расписание: добавлено {rule.Hour:00}:{rule.Minute:00} — {ScheduleItem.ActionLabel(rule.Action)}");
    }

    private void RemoveRule(ScheduleItem item)
    {
        ScheduleItems.Remove(item);
        SaveSchedule();
    }

    private void SaveSchedule()
    {
        _settings.Schedule = ScheduleItems.Select(i => i.Rule).OrderBy(r => r.Hour * 60 + r.Minute).ToList();
        _settings.Save();
        RefreshUpcoming();
        OnPropertyChanged(nameof(HasSchedule));
    }

    private void RefreshUpcoming()
    {
        Upcoming.Clear();
        var now = DateTime.Now;
        foreach (var (at, rule) in Scheduler.Upcoming(ScheduleItems.Select(i => i.Rule), now, 3))
            Upcoming.Add(new UpcomingItem(UpcomingItem.Format(at, now), ScheduleItem.ActionLabel(rule.Action), rule.Action));
        OnPropertyChanged(nameof(HasUpcoming));
    }

    public bool HasUpcoming => Upcoming.Count > 0;

    /// <summary>Раз в 10 секунд: правила расписания, время работы лампы, «далее по расписанию».</summary>
    private void OnClock()
    {
        var now = DateTime.Now;
        var due = Scheduler.Due(ScheduleItems.Select(i => i.Rule), _lastScheduleCheck, now);
        _lastScheduleCheck = now;
        foreach (var rule in due) _ = RunRuleAsync(rule);

        OnPropertyChanged(nameof(UptimeText));
        if (due.Count > 0 || now.Second < 10) RefreshUpcoming(); // раз в минуту хватает
    }

    internal async Task RunRuleAsync(ScheduleRule rule)
    {
        var label = $"{rule.Hour:00}:{rule.Minute:00} — {ScheduleItem.ActionLabel(rule.Action)}";
        if (!IsConnected && rule.Action is not ScheduleAction.SyncStop)
        {
            CommandLog.Write($"Расписание: {label} пропущено — нет связи с лампой");
            return;
        }
        CommandLog.Write($"Расписание: {label}");
        Status = $"По расписанию: {ScheduleItem.ActionLabel(rule.Action).ToLowerInvariant()}";

        switch (rule.Action)
        {
            case ScheduleAction.MainOn:
                MainPower = true;
                if (rule.Temperature is { } k) MainCt = k;
                if (rule.Brightness is { } b) MainBright = b;
                break;
            case ScheduleAction.MainOff:
                MainPower = false;
                break;
            case ScheduleAction.BackOn:
                BgPower = true;
                break;
            case ScheduleAction.BackOff:
                BgPower = false;
                break;
            case ScheduleAction.AllOff:
                MainPower = false;
                BgPower = false;
                break;
            case ScheduleAction.SyncStart:
                if (!IsSyncing) await StartSyncAsync();
                break;
            case ScheduleAction.SyncStop:
                if (IsSyncing) await StopSyncAsync();
                break;
        }
    }
}

/// <summary>Правило в списке расписания.</summary>
public sealed class ScheduleItem : ObservableObject
{
    private readonly Action _changed;

    public ScheduleItem(ScheduleRule rule, Action changed)
    {
        Rule = rule;
        _changed = changed;
    }

    public ScheduleRule Rule { get; }

    public string TimeText => $"{Rule.Hour:00}:{Rule.Minute:00}";
    public string ActionText => ActionLabel(Rule.Action);
    public string DaysText => DaysLabel(Rule.Days);

    /// <summary>«  ·  2700 K · 40 %» — уже с разделителем, чтобы у правил без деталей не висела лишняя точка.</summary>
    public string DetailText => Rule.Action == ScheduleAction.MainOn && (Rule.Brightness != null || Rule.Temperature != null)
        ? "  ·  " + string.Join(" · ", new[]
        {
            Rule.Temperature is { } k ? $"{k} K" : null,
            Rule.Brightness is { } b ? $"{b} %" : null
        }.Where(s => s != null))
        : "";

    public bool Enabled
    {
        get => Rule.Enabled;
        set
        {
            if (Rule.Enabled == value) return;
            Rule.Enabled = value;
            OnPropertyChanged();
            _changed();
        }
    }

    public static string ActionLabel(ScheduleAction a) => a switch
    {
        ScheduleAction.MainOn => "Включить свет",
        ScheduleAction.MainOff => "Выключить свет",
        ScheduleAction.BackOn => "Включить подсветку",
        ScheduleAction.BackOff => "Выключить подсветку",
        ScheduleAction.AllOff => "Выключить всё",
        ScheduleAction.SyncStart => "Запустить синхронизацию",
        ScheduleAction.SyncStop => "Остановить синхронизацию",
        _ => a.ToString()
    };

    public static string DaysLabel(WeekDays d) => d switch
    {
        WeekDays.All => "Каждый день",
        WeekDays.Weekdays => "По будням",
        WeekDays.Weekend => "По выходным",
        WeekDays.None => "Никогда",
        _ => string.Join(", ", DayToggle.Names.Where(n => (d & n.Flag) != 0).Select(n => n.Short))
    };
}

/// <summary>Строка «далее по расписанию»: «Сегодня 22:30 — Выключить всё».</summary>
public sealed record UpcomingItem(string When, string Action, ScheduleAction Kind)
{
    public bool IsOn => Kind is ScheduleAction.MainOn or ScheduleAction.BackOn or ScheduleAction.SyncStart;

    public static string Format(DateTime at, DateTime now)
    {
        var day = (at.Date - now.Date).Days switch
        {
            0 => "Сегодня",
            1 => "Завтра",
            _ => CultureInfo.GetCultureInfo("ru-RU").DateTimeFormat.GetAbbreviatedDayName(at.DayOfWeek)
        };
        return $"{day} {at:HH:mm}";
    }
}

/// <summary>Кнопка дня недели в редакторе правила.</summary>
public sealed class DayToggle(string label, WeekDays flag) : ObservableObject
{
    public static readonly (string Short, WeekDays Flag)[] Names =
    [
        ("Пн", WeekDays.Mon), ("Вт", WeekDays.Tue), ("Ср", WeekDays.Wed), ("Чт", WeekDays.Thu),
        ("Пт", WeekDays.Fri), ("Сб", WeekDays.Sat), ("Вс", WeekDays.Sun)
    ];

    private bool _isOn = true;

    public string Label { get; } = label;
    public WeekDays Flag { get; } = flag;

    public bool IsOn { get => _isOn; set => SetField(ref _isOn, value); }
}

/// <summary>Вариант действия в редакторе правила.</summary>
public sealed record ActionOption(ScheduleAction Action, string Label);

/// <summary>Форма нового правила на странице «Расписание».</summary>
public sealed class ScheduleEditor : ObservableObject
{
    public ScheduleEditor()
    {
        foreach (var (label, flag) in DayToggle.Names) Days.Add(new DayToggle(label, flag));
        _action = Actions[0];
    }

    public ObservableCollection<DayToggle> Days { get; } = [];

    public ActionOption[] Actions { get; } = Enum.GetValues<ScheduleAction>()
        .Select(a => new ActionOption(a, ScheduleItem.ActionLabel(a))).ToArray();

    private string _timeText = "08:00";
    public string TimeText { get => _timeText; set => SetField(ref _timeText, value); }

    private ActionOption _action;
    public ActionOption Action
    {
        get => _action;
        set
        {
            if (!SetField(ref _action, value)) return;
            OnPropertyChanged(nameof(IsMainOn));
        }
    }

    /// <summary>Яркость и температуру можно задать только для «Включить свет».</summary>
    public bool IsMainOn => Action.Action == ScheduleAction.MainOn;

    private bool _setLight = true;
    public bool SetLight { get => _setLight; set => SetField(ref _setLight, value); }

    private double _brightness = 70;
    public double Brightness { get => _brightness; set => SetField(ref _brightness, value); }

    private double _temperature = 4000;
    public double Temperature
    {
        get => _temperature;
        set { if (SetField(ref _temperature, value)) OnPropertyChanged(nameof(TemperatureText)); }
    }

    public string TemperatureText => $"{(int)Math.Round(Temperature / 100) * 100} K";

    private string _error = "";
    public string Error { get => _error; set => SetField(ref _error, value); }

    public bool TryBuild(out ScheduleRule rule, out string error)
    {
        rule = new ScheduleRule();
        error = "";
        if (!TimeSpan.TryParseExact(TimeText.Trim(), [@"h\:mm", @"hh\:mm"], CultureInfo.InvariantCulture, out var time)
            || time.TotalHours >= 24)
        {
            error = "Время — в формате ЧЧ:ММ, например 07:30";
            return false;
        }
        var days = Days.Where(d => d.IsOn).Aggregate(WeekDays.None, (acc, d) => acc | d.Flag);
        if (days == WeekDays.None)
        {
            error = "Выбери хотя бы один день";
            return false;
        }

        rule = new ScheduleRule
        {
            Hour = time.Hours,
            Minute = time.Minutes,
            Days = days,
            Action = Action.Action,
            Brightness = IsMainOn && SetLight ? (int)Math.Round(Brightness) : null,
            Temperature = IsMainOn && SetLight ? (int)Math.Round(Temperature / 100) * 100 : null
        };
        return true;
    }
}
