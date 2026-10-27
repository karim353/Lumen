using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Lumen.Core.Tests;

/// <summary>Поддельная лампа на TCP: принимает одно соединение и отвечает на строки JSON.</summary>
internal sealed class FakeTcpLamp : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private StreamWriter? _writer;

    /// <summary>Ответ на команду (строка JSON) или null — промолчать.</summary>
    public Func<JsonElement, string?> Responder { get; set; } =
        cmd => $$"""{"id":{{cmd.GetProperty("id").GetInt32()}},"result":["ok"]}""";

    public ConcurrentQueue<string> Received { get; } = new();

    public FakeTcpLamp()
    {
        _listener.Start();
        _loop = Task.Run(ServeAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    private async Task ServeAsync()
    {
        try
        {
            using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };

            while (!_cts.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(_cts.Token);
                if (line == null) return;
                Received.Enqueue(line);
                using var doc = JsonDocument.Parse(line);
                var reply = Responder(doc.RootElement);
                if (reply != null) await _writer.WriteLineAsync(reply);
            }
        }
        catch (Exception) { /* тест закончился */ }
    }

    /// <summary>Уведомление от лампы (например, props после нажатия на пульт).</summary>
    public async Task PushAsync(string json)
    {
        for (var i = 0; _writer == null && i < 100; i++) await Task.Delay(10);
        await _writer!.WriteLineAsync(json);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await _loop; } catch { /* ignore */ }
    }
}

/// <summary>Поддельная лампа на UDP: выдаёт токен на udp_sess_new и отвечает на keep-alive.</summary>
internal sealed class FakeUdpLamp : IDisposable
{
    private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly CancellationTokenSource _cts = new();

    public const string Token = "0123456789abcdef0123456789abcdef";

    public ConcurrentQueue<JsonElement> Received { get; } = new();

    /// <summary>false — лампа молчит (нет UDP-сессий у модели).</summary>
    public bool Answer { get; set; } = true;

    /// <summary>Отдавать токен как result[0], а не params.token.</summary>
    public bool TokenInResult { get; set; }

    /// <summary>Сколько первых udp_sess_new проигнорировать (потеря пакета).</summary>
    public int DropFirstSessionRequests { get; set; }

    public FakeUdpLamp() => _ = Task.Run(ServeAsync);

    public IPEndPoint Endpoint => (IPEndPoint)_udp.Client.LocalEndPoint!;

    private async Task ServeAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try { packet = await _udp.ReceiveAsync(_cts.Token); }
            catch { return; }

            var root = JsonDocument.Parse(Encoding.UTF8.GetString(packet.Buffer)).RootElement.Clone();
            Received.Enqueue(root);
            if (!Answer) continue;

            var id = root.GetProperty("id").GetInt32();
            string? reply = root.GetProperty("method").GetString() switch
            {
                "udp_sess_new" when DropFirstSessionRequests-- > 0 => null,
                "udp_sess_new" when TokenInResult => $$"""{"id":{{id}}, "method":"udp_sess_token", "result":["{{Token}}"]}""",
                "udp_sess_new" => $$$"""{"id":{{{id}}}, "method":"udp_sess_token", "params":{"token":"{{{Token}}}"}}""",
                "udp_sess_keep_alive" => $$"""{"id":{{id}}, "result":["ok"], "token":"{{Token}}"}""",
                _ => null // на остальные команды lamp15 не отвечает
            };
            if (reply != null)
                await _udp.SendAsync(Encoding.UTF8.GetBytes(reply + "\r\n"), packet.RemoteEndPoint);
        }
    }

    public IEnumerable<JsonElement> Commands(string method)
        => Received.Where(r => r.GetProperty("method").GetString() == method);

    public void Dispose()
    {
        _cts.Cancel();
        _udp.Dispose();
    }
}

internal static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException("Условие не выполнилось");
            await Task.Delay(10);
        }
    }
}
