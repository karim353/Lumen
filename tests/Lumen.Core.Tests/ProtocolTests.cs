using System.Text.Json;

namespace Lumen.Core.Tests;

public class YeelightClientTests
{
    [Fact]
    public async Task SendAsync_ReturnsResultMatchedById()
    {
        await using var lamp = new FakeTcpLamp
        {
            Responder = cmd => $$"""{"id":{{cmd.GetProperty("id").GetInt32()}},"result":["on","42"]}"""
        };
        await using var client = new YeelightClient("127.0.0.1", lamp.Port);

        var props = await client.GetPropsAsync("power", "bright");

        Assert.Equal("on", props["power"]);
        Assert.Equal("42", props["bright"]);
        Assert.Contains(lamp.Received, l => l.Contains("\"method\":\"get_prop\""));
    }

    [Fact]
    public async Task LampError_BecomesYeelightException()
    {
        await using var lamp = new FakeTcpLamp
        {
            Responder = cmd => $$$"""{"id":{{{cmd.GetProperty("id").GetInt32()}}},"error":{"code":-1,"message":"unsupported method"}}"""
        };
        await using var client = new YeelightClient("127.0.0.1", lamp.Port);

        var ex = await Assert.ThrowsAsync<YeelightException>(() => client.SendAsync("set_music", 1));
        Assert.Equal("unsupported method", ex.Message);
    }

    [Fact]
    public async Task QuotaError_BecomesQuotaException()
    {
        await using var lamp = new FakeTcpLamp
        {
            Responder = cmd => $$$"""{"id":{{{cmd.GetProperty("id").GetInt32()}}},"error":{"code":-1,"message":"client quota exceeded"}}"""
        };
        await using var client = new YeelightClient("127.0.0.1", lamp.Port);

        await Assert.ThrowsAsync<YeelightQuotaException>(() => client.SendAsync("set_bright", 50));
    }

    [Fact]
    public async Task NoReply_TimesOut()
    {
        await using var lamp = new FakeTcpLamp { Responder = _ => null };
        await using var client = new YeelightClient("127.0.0.1", lamp.Port) { Timeout = TimeSpan.FromMilliseconds(300) };

        var ex = await Assert.ThrowsAsync<YeelightException>(() => client.SendAsync("toggle"));
        Assert.Contains("toggle", ex.Message);
    }

    [Fact]
    public async Task PropsNotification_RaisesEvent()
    {
        await using var lamp = new FakeTcpLamp();
        await using var client = new YeelightClient("127.0.0.1", lamp.Port);
        var got = new TaskCompletionSource<IReadOnlyDictionary<string, string>>();
        client.PropsChanged += p => got.TrySetResult(p);
        await client.ConnectAsync();

        await lamp.PushAsync("""{"method":"props","params":{"bg_power":"off","bg_bright":30}}""");
        var props = await got.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal("off", props["bg_power"]);
        Assert.Equal("30", props["bg_bright"]);
    }

    [Fact]
    public void BuildCommand_IsOneJsonObject()
    {
        var json = YeelightClient.BuildCommand(7, "bg_set_rgb", [16711680, "smooth", 50]);
        Assert.Equal("""{"id":7,"method":"bg_set_rgb","params":[16711680,"smooth",50]}""", json);
    }
}

public class UdpRealtimeSessionTests
{
    [Fact]
    public async Task Start_GetsToken_AndCommandsCarryIt()
    {
        using var lamp = new FakeUdpLamp();
        await using var session = await UdpRealtimeSession.StartAsync(lamp.Endpoint, supportsSegments: true);

        await session.SendAsync("set_segment_rgb", 0xFF0000, 0x0000FF);
        await Wait.UntilAsync(() => lamp.Commands("set_segment_rgb").Any());

        var cmd = lamp.Commands("set_segment_rgb").Single();
        Assert.Equal(FakeUdpLamp.Token, cmd.GetProperty("token").GetString());
        Assert.Equal(0xFF0000, cmd.GetProperty("params")[0].GetInt32());
        Assert.True(session.SupportsSegments);
        Assert.True(session.IsAlive);
    }

    [Fact]
    public async Task Start_RetriesLostSessionRequest()
    {
        using var lamp = new FakeUdpLamp { DropFirstSessionRequests = 1 };
        await using var session = await UdpRealtimeSession.StartAsync(lamp.Endpoint, false, default,
            requestTimeout: TimeSpan.FromMilliseconds(200));

        Assert.Equal(2, lamp.Commands("udp_sess_new").Count());
    }

    [Fact]
    public async Task Start_SilentLamp_Throws()
    {
        using var lamp = new FakeUdpLamp { Answer = false };

        await Assert.ThrowsAsync<YeelightException>(() => UdpRealtimeSession.StartAsync(lamp.Endpoint, false, default,
            requestTimeout: TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public async Task KeepAlive_IsSentPeriodically()
    {
        using var lamp = new FakeUdpLamp();
        await using var session = await UdpRealtimeSession.StartAsync(lamp.Endpoint, false, default,
            keepAliveInterval: TimeSpan.FromMilliseconds(100));

        await Wait.UntilAsync(() => lamp.Commands("udp_sess_keep_alive").Count() >= 2);
        var ka = lamp.Commands("udp_sess_keep_alive").First();
        Assert.Equal("keeplive_interval", ka.GetProperty("params")[0].GetString());
        Assert.True(session.IsAlive);
    }

    [Fact]
    public async Task KeepAlive_ReopensSessionWhenLampStopsAnswering()
    {
        using var lamp = new FakeUdpLamp();
        await using var session = await UdpRealtimeSession.StartAsync(lamp.Endpoint, false, default,
            requestTimeout: TimeSpan.FromMilliseconds(50), keepAliveInterval: TimeSpan.FromMilliseconds(50));

        lamp.Answer = false; // лампа перезагрузилась
        await Wait.UntilAsync(() => !session.IsAlive);
        lamp.Answer = true;

        await Wait.UntilAsync(() => lamp.Commands("udp_sess_new").Count() >= 2 && session.IsAlive);
    }

    [Fact]
    public async Task Start_AcceptsTokenInResultArray()
    {
        // Вариант ответа других прошивок: {"method":"udp_sess_token","result":["…"]}
        using var lamp = new FakeUdpLamp { TokenInResult = true };
        await using var session = await UdpRealtimeSession.StartAsync(lamp.Endpoint, false);

        await session.SendAsync("bg_set_bright", 50);
        await Wait.UntilAsync(() => lamp.Commands("bg_set_bright").Any());
        Assert.Equal(FakeUdpLamp.Token, lamp.Commands("bg_set_bright").Single().GetProperty("token").GetString());
    }
}

public class DiscoveryTests
{
    // Реальный ответ Screen Light Bar Pro (lamp15, прошивка 38)
    private const string Lamp15Response =
        "HTTP/1.1 200 OK\r\nCache-Control: max-age=3600\r\nDate: \r\nExt: \r\n" +
        "Location: yeelight://192.168.10.250:55443\r\nServer: POSIX UPnP/1.0 YGLC/1\r\n" +
        "id: 0x000000000ecccd42\r\nmodel: lamp15\r\nfw_ver: 38\r\n" +
        "support: get_prop set_power bg_set_rgb bg_set_bright udp_sess_new udp_sess_keep_alive set_segment_rgb\r\n" +
        "power: on\r\nname: \r\n";

    [Fact]
    public void Parse_Lamp15()
    {
        var info = YeelightDiscovery.Parse(Lamp15Response)!;

        Assert.Equal("192.168.10.250", info.Host);
        Assert.Equal(55443, info.Port);
        Assert.Equal("lamp15", info.Model);
        Assert.Equal("lamp15", info.DisplayName);
        Assert.Contains("udp_sess_new", info.Support);
        Assert.Contains("set_segment_rgb", info.Support);
        Assert.DoesNotContain("set_music", info.Support);
    }

    [Fact]
    public void Parse_IgnoresForeignSsdp()
    {
        Assert.Null(YeelightDiscovery.Parse("HTTP/1.1 200 OK\r\nLocation: http://192.168.1.1:1900/desc.xml\r\n"));
    }
}
