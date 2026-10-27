using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Lumen.Core;

/// <summary>
/// Music mode: мы поднимаем TCP-сервер, командой set_music просим лампу подключиться к нему,
/// и дальше шлём команды в этот сокет. Лимита ~60 команд/мин нет, ответов лампа не присылает.
/// Нужен для синхронизации с экраном (20–30 обновлений в секунду) на классических лампах.
/// Screen Light Bar Pro (lamp15) music mode не умеет — у неё <see cref="UdpRealtimeSession"/>.
/// </summary>
public sealed class MusicModeSession : IRealtimeChannel
{
    private readonly YeelightClient _control;
    private readonly TcpListener _listener;
    private readonly TcpClient _lamp;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _nextId = 100_000;
    private bool _disposed;

    private MusicModeSession(YeelightClient control, TcpListener listener, TcpClient lamp)
    {
        _control = control;
        _listener = listener;
        _lamp = lamp;
        _lamp.NoDelay = true;
        _stream = lamp.GetStream();
    }

    public bool IsAlive => !_disposed && _lamp.Connected;
    public string Name => "music mode";
    public bool SupportsSegments => false;

    public static async Task<MusicModeSession> StartAsync(YeelightClient control, CancellationToken ct = default)
    {
        var localIp = GetLocalAddressFor(control.Host);
        var listener = new TcpListener(localIp, 0);
        listener.Start(1);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Task<TcpClient>? accept = null;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

            accept = listener.AcceptTcpClientAsync(timeoutCts.Token).AsTask();
            await control.SendAsync("set_music", 1, localIp.ToString(), port).ConfigureAwait(false);

            TcpClient lamp;
            try
            {
                lamp = await accept.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new YeelightException(
                    "Лампа не подключилась обратно. Разреши Lumen входящие подключения в брандмауэре Windows.");
            }

            return new MusicModeSession(control, listener, lamp);
        }
        catch
        {
            listener.Stop();
            // Если упал set_music, accept остался висеть и упадёт после Stop — гасим его ошибку
            accept?.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            throw;
        }
    }

    /// <summary>Отправить команду без ожидания ответа.</summary>
    public async Task SendAsync(string method, params object[] parameters)
    {
        if (_disposed) return;
        var id = Interlocked.Increment(ref _nextId);
        var bytes = Encoding.UTF8.GetBytes(YeelightClient.BuildCommand(id, method, parameters) + "\r\n");

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(bytes).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            throw new YeelightException("Music mode: соединение разорвано");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Локальный IP, через который ПК ходит к лампе (нужен лампе для обратного подключения).</summary>
    public static IPAddress GetLocalAddressFor(string host)
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Connect(IPAddress.Parse(host), YeelightClient.DefaultPort); // UDP connect ничего не отправляет
        return ((IPEndPoint)probe.LocalEndPoint!).Address;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { await _control.SendAsync("set_music", 0).ConfigureAwait(false); }
        catch { /* лампа могла уже отвалиться */ }
        _stream.Dispose();
        _lamp.Dispose();
        _listener.Stop();
    }
}
