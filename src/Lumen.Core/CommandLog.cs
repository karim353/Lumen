using System.Text;

namespace Lumen.Core;

/// <summary>
/// Журнал общения с лампой: %AppData%\Lumen\lumen.log. Что ушло (TCP/UDP), что лампа ответила
/// и прислала сама (props), подключения и события системы. Нужен, чтобы разбираться,
/// кто и когда поменял состояние лампы. Кадры синхронизации (до 30/с) сюда не пишутся.
/// Больше 1 МБ — файл переезжает в lumen.old.log и начинается заново.
/// </summary>
public static class CommandLog
{
    private const long MaxBytes = 1024 * 1024;
    private const int MaxLinesPerSecond = 40; // защита от лавины props во время синхронизации

    private static readonly object Gate = new();
    private static StreamWriter? _writer;
    private static long _second;
    private static int _linesThisSecond, _dropped;

    /// <summary>Папка журнала. Тесты подменяют её на временную.</summary>
    public static string Folder { get; set; } = AppSettings.Folder;

    public static bool Enabled { get; set; } = true;

    public static string FilePath => Path.Combine(Folder, "lumen.log");

    /// <summary>Записать строку. Никогда не бросает: журнал не должен ронять приложение.</summary>
    public static void Write(string text)
    {
        if (!Enabled) return;
        try
        {
            lock (Gate)
            {
                var now = DateTime.Now;
                var second = now.Ticks / TimeSpan.TicksPerSecond;
                if (second != _second)
                {
                    _second = second;
                    _linesThisSecond = 0;
                    if (_dropped > 0)
                    {
                        WriteLine(now, $"… пропущено строк: {_dropped}");
                        _dropped = 0;
                    }
                }
                if (++_linesThisSecond > MaxLinesPerSecond)
                {
                    _dropped++;
                    return;
                }
                WriteLine(now, text);
            }
        }
        catch
        {
            // Диск занят/нет прав — просто не пишем
            _writer?.Dispose();
            _writer = null;
        }
    }

    public static void Write(string direction, string method, object[] parameters)
        => Write($"{direction} {method} {Format(parameters)}");

    /// <summary>Параметры команды как в протоколе: [1,"smooth",300].</summary>
    public static string Format(object[] parameters)
    {
        var sb = new StringBuilder("[");
        for (var i = 0; i < parameters.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(parameters[i] is string s ? $"\"{s}\"" : Convert.ToString(parameters[i], System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.Append(']').ToString();
    }

    private static void WriteLine(DateTime now, string text)
    {
        if (_writer == null)
        {
            Directory.CreateDirectory(Folder);
            Rotate();
            _writer = new StreamWriter(new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete),
                new UTF8Encoding(false)) { AutoFlush = true };
        }

        _writer.WriteLine($"{now:yyyy-MM-dd HH:mm:ss.fff}  {text}");

        if (_writer.BaseStream.Length > MaxBytes)
        {
            _writer.Dispose();
            _writer = null; // следующая запись повернёт файл
        }
    }

    private static void Rotate()
    {
        var file = new FileInfo(FilePath);
        if (!file.Exists || file.Length <= MaxBytes) return;
        File.Move(FilePath, Path.Combine(Folder, "lumen.old.log"), overwrite: true);
    }

    /// <summary>Закрыть файл (выход из приложения, тесты).</summary>
    public static void Close()
    {
        lock (Gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
