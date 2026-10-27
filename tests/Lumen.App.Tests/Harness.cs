using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows.Threading;
using Lumen.Core;
using Lumen.Core.Tests;
using Lumen.ViewModels;

// Все тесты модели пишут настройки в одну временную папку (AppSettings.Folder — статическое),
// поэтому параллельно их запускать нельзя: один тест прочитал бы файл другого
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Lumen.App.Tests;

internal static class TestSetup
{
    /// <summary>
    /// Настройки и журнал тестов — во временную папку. Без этого модель сохраняла бы
    /// настройки в настоящий %AppData%\Lumen и затирала список ламп пользователя.
    /// </summary>
    [ModuleInitializer]
    internal static void Redirect()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Lumen.App.Tests", Environment.ProcessId.ToString());
        AppSettings.Folder = folder;
        CommandLog.Folder = folder;
    }
}

/// <summary>
/// Тест модели на «UI-потоке»: STA-поток с диспетчером и его контекстом синхронизации —
/// как в приложении, иначе продолжения async-методов и OnUi() уходили бы в никуда.
/// </summary>
internal static class UiThread
{
    public static void Run(Func<Task> test, int timeoutMs = 30_000)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.InvokeAsync(async () =>
            {
                try { await test(); }
                catch (Exception ex) { error = ex; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        if (!thread.Join(timeoutMs)) throw new TimeoutException("Тест не уложился во время");
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}

/// <summary>Модель, подключённая к поддельной лампе. Команды лампе — в <see cref="Commands"/>.</summary>
internal sealed class Rig : IAsyncDisposable
{
    public const string Host = "10.0.0.5";

    // Порядок как в MainViewModel.StateProps: power, bright, ct, bg_power, bg_bright, bg_lmode, bg_rgb, bg_hue, bg_sat
    public static readonly string[] DefaultState = ["on", "35", "3500", "off", "70", "3", "8651007", "271", "100"];

    public FakeTcpLamp Lamp { get; } = new();
    public AppSettings Settings { get; } = new();
    public MainViewModel Vm { get; private set; } = null!;

    /// <summary>Свой ответ на команду (null — обычный «ok»); вызывается для всего, кроме get_prop.</summary>
    public Func<string, JsonElement, string?>? Reply { get; set; }

    public static async Task<Rig> StartAsync(string[]? state = null, IReadOnlySet<string>? support = null)
    {
        var rig = new Rig();
        var props = state ?? DefaultState;
        rig.Lamp.Responder = cmd =>
        {
            var id = cmd.GetProperty("id").GetInt32();
            var method = cmd.GetProperty("method").GetString()!;
            if (method == "get_prop")
                return JsonSerializer.Serialize(new { id, result = props });
            return rig.Reply?.Invoke(method, cmd) ?? $$"""{"id":{{id}},"result":["ok"]}""";
        };

        rig.Settings.Devices.Add(new SavedDevice { Host = Host, Model = "lamp15" });
        rig.Settings.SelectedHost = Host;
        rig.Vm = new MainViewModel(rig.Settings, Dispatcher.CurrentDispatcher)
        {
            ClientFactory = _ => new YeelightClient("127.0.0.1", rig.Lamp.Port),
            // Без udp_sess_new: команды подсветки идут по TCP и видны поддельной лампе
            Probe = _ => Task.FromResult<YeelightDeviceInfo?>(new YeelightDeviceInfo("0x0000000001", Host, 55443, "lamp15", "", "38",
                support ?? new HashSet<string> { "get_prop", "set_power", "set_bright", "set_ct_abx", "bg_set_rgb", "bg_set_scene", "set_name" }))
        };
        await rig.Vm.InitializeAsync();
        await Wait.UntilAsync(() => rig.Vm.IsConnected);
        return rig;
    }

    /// <summary>Команды, которые модель отправила лампе (без опроса состояния).</summary>
    public IReadOnlyList<(string Method, JsonElement Params)> Commands
        => Lamp.Received
            .Select(line => JsonDocument.Parse(line).RootElement)
            .Select(e => (e.GetProperty("method").GetString()!, e.GetProperty("params").Clone()))
            .Where(c => c.Item1 != "get_prop")
            .ToList();

    public bool Sent(string method, Func<JsonElement, bool>? match = null)
        => Commands.Any(c => c.Method == method && (match == null || match(c.Params)));

    public Task WaitSentAsync(string method, Func<JsonElement, bool>? match = null, int timeoutMs = 5000)
        => Wait.UntilAsync(() => Sent(method, match), timeoutMs);

    public async ValueTask DisposeAsync()
    {
        await Vm.ShutdownAsync();
        await Lamp.DisposeAsync();
    }
}
