using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Lumen.Core;

/// <summary>
/// UDP-сессия Yeelight (порт 55444) — так синхронизирует Yeelight Station на Screen Light Bar Pro,
/// у которой нет music mode. udp_sess_new выдаёт токен, дальше каждая команда уходит датаграммой
/// с полем "token". Отвечает лампа только на udp_sess_new и udp_sess_keep_alive.
/// Сессия живёт, пока раз в несколько секунд приходит keep-alive; одновременно их не больше четырёх.
/// </summary>
public sealed class UdpRealtimeSession : IRealtimeChannel
{
    public const int DefaultPort = 55444;

    private const int KeepAliveSeconds = 10; // столько лампа ждёт следующий keep-alive

    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private TaskCompletionSource<string>? _tokenWaiter;
    private string _token = "";
    private int _nextId;
    private long _lastReplyTicks;
    private Task? _receiveLoop;
    private Task? _keepAliveLoop;
    private bool _disposed;
    private bool _lastRenewOk = true;

    private UdpRealtimeSession(UdpClient udp, bool supportsSegments)
    {
        _udp = udp;
        SupportsSegments = supportsSegments;
    }

    public string Name => "UDP";
    public bool SupportsSegments { get; }

    /// <summary>Лампа отвечала на keep-alive недавно.</summary>
    public bool IsAlive => !_disposed && DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastReplyTicks) < DeadAfter.Ticks;

    internal TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMilliseconds(800);
    internal TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(4);
    internal TimeSpan DeadAfter => KeepAliveInterval * 3 + RequestTimeout;

    public static Task<UdpRealtimeSession> StartAsync(string host, bool supportsSegments, CancellationToken ct = default)
        => StartAsync(new IPEndPoint(IPAddress.Parse(host), DefaultPort), supportsSegments, ct);

    internal static async Task<UdpRealtimeSession> StartAsync(
        IPEndPoint lamp, bool supportsSegments, CancellationToken ct = default,
        TimeSpan? requestTimeout = null, TimeSpan? keepAliveInterval = null)
    {
        var udp = new UdpClient(AddressFamily.InterNetwork);
        UdpRealtimeSession? session = null;
        try
        {
            udp.Connect(lamp);
            DisableConnectionReset(udp.Client);

            session = new UdpRealtimeSession(udp, supportsSegments)
            {
                RequestTimeout = requestTimeout ?? TimeSpan.FromMilliseconds(800),
                KeepAliveInterval = keepAliveInterval ?? TimeSpan.FromSeconds(4)
            };
            session._receiveLoop = Task.Run(() => session.ReceiveLoopAsync(session._cts.Token));

            if (!await session.OpenSessionAsync(attempts: 3, ct).ConfigureAwait(false))
            {
                CommandLog.Write($"[{lamp.Address}] UDP: сессия не открылась");
                throw new YeelightException($"Лампа {lamp.Address} не открыла UDP-сессию (порт {lamp.Port})");
            }
            CommandLog.Write($"[{lamp.Address}] UDP: сессия открыта");

            session._keepAliveLoop = Task.Run(() => session.KeepAliveLoopAsync(session._cts.Token));
            return session;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            if (session != null) await session.DisposeAsync().ConfigureAwait(false);
            else udp.Dispose();
            throw new YeelightException("UDP-сессия: " + ex.Message);
        }
        catch
        {
            if (session != null) await session.DisposeAsync().ConfigureAwait(false);
            else udp.Dispose();
            throw;
        }
    }

    /// <summary>Отправить команду датаграммой. Ответа нет — потерянный кадр просто перекроет следующий.</summary>
    public async Task SendAsync(string method, params object[] parameters)
    {
        if (_disposed) return;
        var id = Interlocked.Increment(ref _nextId);
        string token;
        lock (_gate) token = _token;

        var json = JsonSerializer.Serialize(new { id, method, @params = parameters, token });
        try
        {
            await _udp.SendAsync(Encoding.UTF8.GetBytes(json + "\r\n")).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            if (!_disposed) throw new YeelightException("UDP-сессия: " + ex.Message);
        }
    }

    /// <summary>udp_sess_new с повторами: UDP может потеряться.</summary>
    private async Task<bool> OpenSessionAsync(int attempts, CancellationToken ct)
    {
        for (var i = 0; i < attempts && !ct.IsCancellationRequested; i++)
        {
            var waiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) _tokenWaiter = waiter;

            await SendRawAsync(new { id = Interlocked.Increment(ref _nextId), method = "udp_sess_new", @params = Array.Empty<object>() })
                .ConfigureAwait(false);
            try
            {
                var token = await waiter.Task.WaitAsync(RequestTimeout, ct).ConfigureAwait(false);
                lock (_gate) _token = token;
                return true;
            }
            catch (TimeoutException) { /* следующая попытка */ }
        }
        return false;
    }

    private async Task KeepAliveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(KeepAliveInterval, ct).ConfigureAwait(false);
                // Имя параметра с опечаткой — так в протоколе Yeelight
                await SendAsync("udp_sess_keep_alive", "keeplive_interval", KeepAliveSeconds.ToString()).ConfigureAwait(false);

                // Лампа перестала отвечать (перезагрузка, Wi-Fi) — токен протух, открываем сессию заново
                if (!IsAlive)
                {
                    var ok = await OpenSessionAsync(attempts: 1, ct).ConfigureAwait(false);
                    if (ok != _lastRenewOk)
                        CommandLog.Write(ok ? "UDP: лампа снова отвечает, сессия открыта заново" : "UDP: лампа не отвечает на keep-alive");
                    _lastRenewOk = ok;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception)
            {
                if (_disposed) return; // сеть моргнула — попробуем на следующем круге
            }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try
            {
                packet = await _udp.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { if (_disposed) return; continue; }

            HandleReply(Encoding.UTF8.GetString(packet.Buffer));
        }
    }

    /// <summary>
    /// {"id":1,"method":"udp_sess_token","params":{"token":"…"}} — новая сессия;
    /// {"id":2,"result":["ok"],"token":"…"} — ответ на keep-alive.
    /// </summary>
    internal void HandleReply(string text)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch (JsonException) { return; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            string? token = null;
            if (root.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object
                && p.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String)
                token = t.GetString();
            else if (root.TryGetProperty("method", out var m) && m.GetString() == "udp_sess_token"
                     && root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Array
                     && r.GetArrayLength() > 0 && r[0].ValueKind == JsonValueKind.String)
                token = r[0].GetString();

            if (root.TryGetProperty("error", out _) && token == null) return;

            Interlocked.Exchange(ref _lastReplyTicks, DateTime.UtcNow.Ticks);
            if (string.IsNullOrEmpty(token)) return;

            TaskCompletionSource<string>? waiter;
            lock (_gate)
            {
                waiter = _tokenWaiter;
                _tokenWaiter = null;
            }
            waiter?.TrySetResult(token);
        }
    }

    private async Task SendRawAsync(object message)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message) + "\r\n");
        try { await _udp.SendAsync(bytes).ConfigureAwait(false); }
        catch (SocketException) { /* ответа не будет — сработает таймаут */ }
    }

    /// <summary>
    /// Windows по ICMP «порт недоступен» роняет следующий ReceiveAsync с ConnectionReset.
    /// Для UDP это бессмысленно — отключаем (SIO_UDP_CONNRESET).
    /// </summary>
    private static void DisableConnectionReset(Socket socket)
    {
        if (!OperatingSystem.IsWindows()) return;
        const int SIO_UDP_CONNRESET = -1744830452;
        try { socket.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null); }
        catch { /* не критично */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _udp.Dispose(); // явного закрытия нет: без keep-alive лампа сама забудет сессию
        try { if (_receiveLoop != null) await _receiveLoop.ConfigureAwait(false); } catch { /* ignore */ }
        try { if (_keepAliveLoop != null) await _keepAliveLoop.ConfigureAwait(false); } catch { /* ignore */ }
        _cts.Dispose();
    }
}
