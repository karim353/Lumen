using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Microsoft.Win32;

namespace Lumen.Core;

public sealed class SavedDevice
{
    public string Host { get; set; } = "";
    public string Name { get; set; } = "";
    public string Model { get; set; } = "";
}

public sealed class AppSettings
{
    public List<SavedDevice> Devices { get; set; } = [];
    public string? SelectedHost { get; set; }
    public bool StartMinimized { get; set; } = true;
    public bool TrayHintShown { get; set; }

    public int SyncFps { get; set; } = 20;
    public double SyncSmoothing { get; set; } = 0.5;
    public double SyncSaturation { get; set; } = 1.3;
    public bool SyncFollowBrightness { get; set; } = true;
    public bool SyncZones { get; set; }
    public bool SyncZonesSwap { get; set; }

    /// <summary>Синхронизация шла, когда Lumen закрыли, — запустить её снова при старте.</summary>
    public bool SyncWasRunning { get; set; }
    public string? SyncMonitor { get; set; }

    public double EffectSpeed { get; set; } = 1.0;

    /// <summary>Меньше анимаций интерфейса — меньше нагрузка на процессор.</summary>
    public bool EnergySaving { get; set; }

    public List<ScheduleRule> Schedule { get; set; } = [];

    // Кириллица (имена ламп) — как есть, а не Ст…: файл удобно читать и править руками
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    /// <summary>%AppData%\Lumen. Тесты подменяют папку на временную, чтобы не трогать настоящие настройки.</summary>
    public static string Folder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lumen");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* битый файл — начинаем с чистого листа */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(tmp, FilePath, overwrite: true); // атомарная запись
        }
        catch { /* настройки не критичны */ }
    }
}

/// <summary>Автозапуск через HKCU\...\Run — права администратора не нужны.</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Lumen";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled, string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command(exePath));
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// Если автозапуск включён, но его exe больше нет (Lumen перенесли или удалили папку) —
    /// переписываем путь на текущий. Рабочий путь не трогаем: разовый запуск переносной
    /// копии не должен уводить автозапуск с установленного Lumen.
    /// </summary>
    public static void Refresh(string exePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) is string current && !File.Exists(ExeOf(current)))
            key.SetValue(ValueName, Command(exePath));
    }

    private static string Command(string exePath) => $"\"{exePath}\" --minimized";

    /// <summary>Путь к exe из строки запуска: <c>"C:\...\Lumen.exe" --minimized</c>.</summary>
    internal static string ExeOf(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            return end > 0 ? command[1..end] : command.Trim('"');
        }
        var space = command.IndexOf(' ');
        return space > 0 ? command[..space] : command;
    }
}
