using System;
using System.Linq;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using Lumen.Core;
using Lumen.Services;
using Lumen.ViewModels;
using Lumen.Views;

namespace Lumen;

public partial class App : Application
{
    private const string MutexName = "Lumen.SingleInstance";
    private const string ShowEventName = "Lumen.ShowWindow";
    // Установщик и деинсталлятор взводят это событие, чтобы Lumen закрылся сам: с остановкой синхронизации и сохранением настроек
    private const string ExitEventName = "Lumen.Exit";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private EventWaitHandle? _exitEvent;
    private AppSettings _settings = new();
    private MainViewModel? _vm;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private TrayFlyout? _flyout;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Одна копия: повторный запуск просто показывает уже открытое окно
        _mutex = new Mutex(true, MutexName, out var isFirst);
        if (!isFirst)
        {
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { /* ignore */ }
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _exitEvent = new EventWaitHandle(false, EventResetMode.ManualReset, ExitEventName);
        _exitEvent.Reset(); // взведённое событие могло остаться от установщика, который ещё держит его открытым
        var signals = new WaitHandle[] { _showEvent, _exitEvent };
        new Thread(() =>
        {
            while (true)
            {
                if (WaitHandle.WaitAny(signals) == 1)
                {
                    Dispatcher.InvokeAsync(ExitApp);
                    return;
                }
                Dispatcher.InvokeAsync(ShowMainWindow);
            }
        }) { IsBackground = true, Name = "Lumen.SignalListener" }.Start();

        _settings = AppSettings.Load();
        CommandLog.Write($"Lumen запущен{(e.Args.Length > 0 ? " " + string.Join(" ", e.Args) : "")}");
        if (Environment.ProcessPath is { } exe)
            try { Autostart.Refresh(exe); } catch { /* не критично */ }
        _vm = new MainViewModel(_settings, Dispatcher);

        // Блокировка экрана и сон — синхронизация на паузу, после — продолжаем
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _window = new MainWindow { DataContext = _vm };
        _window.HiddenToTray += OnHiddenToTray;

        _flyout = new TrayFlyout { DataContext = _vm };
        _flyout.OpenRequested += ShowMainWindow;
        _tray = new TrayIcon(_vm, ShowMainWindow, () => _flyout?.Toggle(), ExitApp);

        var startHidden = e.Args.Contains("--minimized") && _settings.StartMinimized;
        if (!startHidden) _window.Show();

        await _vm.InitializeAsync();
    }

    public void ShowMainWindow() => _window?.ShowFromTray();

    // SystemEvents приходят в своём потоке — переходим в UI
    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e) => Dispatcher.InvokeAsync(() =>
    {
        if (_vm == null) return;
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.ConsoleDisconnect:
            case SessionSwitchReason.RemoteConnect:
                _vm.PauseForSystem("экран заблокирован", isLock: true);
                break;
            case SessionSwitchReason.SessionUnlock:
            case SessionSwitchReason.ConsoleConnect:
                _ = _vm.ResumeAfterSystemAsync("экран разблокирован", isUnlock: true);
                break;
        }
    });

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e) => Dispatcher.InvokeAsync(() =>
    {
        if (_vm == null) return;
        if (e.Mode == PowerModes.Suspend) _vm.PauseForSystem("компьютер засыпает", isLock: false);
        else if (e.Mode == PowerModes.Resume) _ = _vm.ResumeAfterSystemAsync("компьютер проснулся", isUnlock: false);
    });

    private void OnHiddenToTray()
    {
        if (_settings.TrayHintShown) return;
        _settings.TrayHintShown = true;
        _settings.Save();
        _tray?.ShowHint("Lumen работает в фоне", "Клик по иконке — быстрая панель, двойной клик — окно, правый — меню и выход.");
    }

    public async void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;

        _tray?.Dispose();
        _tray = null;
        _flyout?.Close();

        if (_window != null)
        {
            _window.AllowClose = true;
            _window.Close();
        }

        if (_vm != null)
        {
            try { await _vm.ShutdownAsync(); } catch { /* выходим в любом случае */ }
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _settings.Save();
        CommandLog.Write("Lumen закрыт");
        CommandLog.Close();
        _showEvent?.Dispose();
        _exitEvent?.Dispose();
        if (_mutex != null)
        {
            try { _mutex.ReleaseMutex(); } catch { /* ignore */ }
            _mutex.Dispose();
        }
        base.OnExit(e);
    }
}
