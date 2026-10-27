using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Lumen.Core;

/// <summary>
/// Клиент протокола Yeelight LAN: TCP 55443, одна JSON-команда на строку (\r\n).
/// Ответы сопоставляются с запросами по id, уведомления "props" приходят событием.
/// </summary>
public sealed class YeelightClient : IAsyncDisposable
{
    public const int DefaultPort = 55443;

    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private CancellationTokenSource? _readCts;
    private int _nextId;

    public YeelightClient(string host, int port = DefaultPort)
    {
        Host = host;
        Port = port;
    }

    public string Host { get; }
    public int Port { get; }
    public bool IsConnected => _tcp?.Connected == true && _stream != null;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Лампа сообщила об изменении свойств (пульт, другое приложение, наша же команда).</summary>
    public event Action<IReadOnlyDictionary<string, string>>? PropsChanged;

    /// <summary>Соединение установлено (true) или потеряно (false).</summary>
    public event Action<bool>? ConnectionChanged;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsConnected) return;
            CloseSocket();

            var tcp = new TcpClient { NoDelay = true };
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(Timeout);
            try
            {
                await tcp.ConnectAsync(Host, Port, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                tcp.Dispose();
                CommandLog.Write($"[{Host}] TCP: нет ответа на подключение");
                throw new YeelightException($"Лампа {Host}:{Port} не отвечает");
            }
            catch (SocketException ex)
            {
                tcp.Dispose();
                CommandLog.Write($"[{Host}] TCP: ошибка подключения {ex.SocketErrorCode}");
                throw new YeelightException($"Не удалось подключиться к {Host}:{Port}: {ex.SocketErrorCode}");
            }

            _tcp = tcp;
            _stream = tcp.GetStream();
            _readCts = new CancellationTokenSource();
            _ = Task.Run(() => ReadLoopAsync(_stream, _readCts.Token));
            CommandLog.Write($"[{Host}] TCP: подключено");
        }
        finally
        {
            _connectLock.Release();
        }

        ConnectionChanged?.Invoke(true);
    }

    /// <summary>Отправить команду и дождаться ответа. Возвращает массив result.</summary>
    public async Task<JsonElement> SendAsync(string method, params object[] parameters)
    {
        if (!IsConnected) await ConnectAsync().ConfigureAwait(false);

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        try
        {
            await WriteLineAsync(BuildCommand(id, method, parameters)).ConfigureAwait(false);

            return await tcs.Task.WaitAsync(Timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new YeelightException($"Нет ответа на {method}");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Запросить свойства. Возвращает словарь имя → значение (строкой, как отдаёт лампа).</summary>
    public async Task<Dictionary<string, string>> GetPropsAsync(params string[] names)
    {
        var result = await SendAsync("get_prop", names.Cast<object>().ToArray()).ConfigureAwait(false);
        var dict = new Dictionary<string, string>();
        var i = 0;
        foreach (var item in result.EnumerateArray())
        {
            if (i >= names.Length) break;
            dict[names[i++]] = item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : item.ToString();
        }
        return dict;
    }

    internal static string BuildCommand(int id, string method, object[] parameters)
        => JsonSerializer.Serialize(new { id, method, @params = parameters });

    private async Task WriteLineAsync(string json)
    {
        var stream = _stream ?? throw new YeelightException("Нет соединения");
        var bytes = Encoding.UTF8.GetBytes(json + "\r\n");

        CommandLog.Write($"[{Host}] TCP → {json}");
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(bytes).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            HandleDisconnect();
            throw new YeelightException("Соединение с лампой разорвано");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null) break; // лампа закрыла соединение
                if (line.Length == 0) continue;
                HandleMessage(line);
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception) { /* сеть упала — ниже обработаем как разрыв */ }

        if (!ct.IsCancellationRequested) HandleDisconnect();
    }

    private void HandleMessage(string line)
    {
        CommandLog.Write($"[{Host}] TCP ← {line}");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) { return; }

        using (doc)
        {
            var root = doc.RootElement;

            if (root.TryGetProperty("method", out var m) && m.GetString() == "props"
                && root.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object)
            {
                var props = new Dictionary<string, string>();
                foreach (var prop in p.EnumerateObject())
                    props[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString() ?? ""
                        : prop.Value.ToString();
                PropsChanged?.Invoke(props);
                return;
            }

            if (!root.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id)) return;
            if (!_pending.TryGetValue(id, out var tcs)) return;

            if (root.TryGetProperty("error", out var err))
            {
                var msg = (err.TryGetProperty("message", out var em) ? em.GetString() : err.ToString()) ?? "Ошибка лампы";
                // "client quota exceeded" — лампа принимает ~60 команд в минуту на соединение
                tcs.TrySetException(msg.Contains("quota", StringComparison.OrdinalIgnoreCase)
                    ? new YeelightQuotaException(msg)
                    : new YeelightException(msg));
            }
            else if (root.TryGetProperty("result", out var res))
            {
                tcs.TrySetResult(res.Clone());
            }
        }
    }

    private void HandleDisconnect()
    {
        var wasConnected = _stream != null;
        if (wasConnected) CommandLog.Write($"[{Host}] TCP: соединение разорвано");
        CloseSocket();
        foreach (var kv in _pending)
            kv.Value.TrySetException(new YeelightException("Соединение с лампой разорвано"));
        if (wasConnected) ConnectionChanged?.Invoke(false);
    }

    private void CloseSocket()
    {
        try { _readCts?.Cancel(); } catch { /* ignore */ }
        _readCts?.Dispose();
        _readCts = null;
        _stream?.Dispose();
        _stream = null;
        _tcp?.Dispose();
        _tcp = null;
    }

    public ValueTask DisposeAsync()
    {
        PropsChanged = null;
        ConnectionChanged = null;
        CloseSocket();
        foreach (var kv in _pending)
            kv.Value.TrySetCanceled();
        return ValueTask.CompletedTask;
    }
}

public class YeelightException(string message) : Exception(message);

/// <summary>Лампа отклонила команду из-за лимита частоты — её стоит повторить чуть позже.</summary>
public sealed class YeelightQuotaException(string message) : YeelightException(message);
