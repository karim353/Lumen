using Lumen.Core;
using Lumen.Core.Tests;
using Lumen.ViewModels;

namespace Lumen.App.Tests;

public class ConnectionTests
{
    [Fact]
    public void Connect_AppliesLampStateAndInfo() => UiThread.Run(async () =>
    {
        await using var rig = await Rig.StartAsync();
        var vm = rig.Vm;

        Assert.True(vm.MainPower);
        Assert.Equal(35, vm.MainBright);
        Assert.Equal(3500, vm.MainCt);
        Assert.False(vm.BgPower);
        Assert.Equal(70, vm.BgBright);
        Assert.Equal(271, vm.BgHue);

        await Wait.UntilAsync(() => vm.FirmwareText == "v38");
        Assert.Equal("Screen Light Bar Pro", vm.ModelName);
        Assert.True(vm.CanUseZones == false); // в поддельном support нет set_segment_rgb
    });

    [Fact]
    public void Settings_GoToTempFolder_NotRealAppData()
    {
        // Страховка от регрессии: однажды тестовый стенд уже затёр настоящий settings.json
        Assert.StartsWith(Path.GetTempPath(), AppSettings.Folder, StringComparison.OrdinalIgnoreCase);
    }
}

public class BacklightTests
{
    [Fact]
    public void BgPowerOn_UsesScene_NotSetPower() => UiThread.Run(async () =>
    {
        // Регрессия: lamp15, выключенная в режиме HSV, игнорирует bg_set_power on — включаем сценой
        await using var rig = await Rig.StartAsync();

        rig.Vm.BgPower = true;

        await rig.WaitSentAsync("bg_set_scene", p => p[0].GetString() == "color");
        Assert.False(rig.Sent("bg_set_power", p => p[0].GetString() == "on"));
    });

    [Fact]
    public void HueChange_SendsRgb_NotHsv() => UiThread.Run(async () =>
    {
        // Регрессия: bg_set_hsv переводит лампу в HSV, после чего её нельзя включить
        await using var rig = await Rig.StartAsync();

        rig.Vm.BgHue = 120; // насыщенность 100 → чистый зелёный

        await rig.WaitSentAsync("bg_set_rgb", p => p[0].GetInt32() == 0x00FF00);
        Assert.False(rig.Sent("bg_set_hsv"));
    });

    [Fact]
    public void LampEcho_RightAfterSliderMove_IsIgnored() => UiThread.Run(async () =>
    {
        await using var rig = await Rig.StartAsync();
        var vm = rig.Vm;

        vm.BgBright = 60;
        // Лампа присылает «эхо» прошлого значения, пока слайдер ещё в движении
        await rig.Lamp.PushAsync("""{"method":"props","params":{"bg_bright":"30"}}""");
        await Task.Delay(150);
        Assert.Equal(60, vm.BgBright);

        // Позже — это уже настоящее изменение (пульт), его применяем
        await Task.Delay(800);
        await rig.Lamp.PushAsync("""{"method":"props","params":{"bg_bright":"30"}}""");
        await Wait.UntilAsync(() => vm.BgBright == 30);
    });
}

public class MainLightTests
{
    [Fact]
    public void QuotaExceeded_FinalSliderValueStillArrives() => UiThread.Run(async () =>
    {
        await using var rig = await Rig.StartAsync();
        var refused = 0;
        rig.Reply = (method, cmd) =>
        {
            if (method != "set_bright" || Interlocked.Increment(ref refused) > 1) return null;
            var id = cmd.GetProperty("id").GetInt32();
            return $$$"""{"id":{{{id}}},"error":{"code":-1,"message":"client quota exceeded"}}""";
        };

        rig.Vm.MainBright = 40;

        await Wait.UntilAsync(() => rig.Vm.Status.Contains("лимит"), 3000);
        await Wait.UntilAsync(() => rig.Commands.Count(c => c.Method == "set_bright" && c.Params[0].GetInt32() == 40) == 2, 6000);
        await Wait.UntilAsync(() => !rig.Vm.Status.Contains("лимит"), 3000);
    });

    [Fact]
    public void CtPreset_FollowsTemperature() => UiThread.Run(async () =>
    {
        await using var rig = await Rig.StartAsync();
        rig.Vm.MainCt = 4010;
        Assert.Equal("4000", rig.Vm.CtPreset);
        rig.Vm.MainCt = 3300;
        Assert.Equal("", rig.Vm.CtPreset);
    });
}

public class DeviceTests
{
    [Fact]
    public void Rename_SendsSetName_AndSaves() => UiThread.Run(async () =>
    {
        await using var rig = await Rig.StartAsync();
        var vm = rig.Vm;

        vm.StartRenameCommand.Execute(null);
        vm.RenameText = "Стол";
        vm.SaveRenameCommand.Execute(null);

        await rig.WaitSentAsync("set_name", p => p[0].GetString() == "Стол");
        await Wait.UntilAsync(() => vm.SelectedDevice?.Title == "Стол");
        Assert.Equal("Стол", rig.Settings.Devices.Single().Name);
        Assert.Equal("Стол", AppSettings.Load().Devices.Single().Name);                     // сохранилось на диск
        Assert.Contains("Стол", File.ReadAllText(Path.Combine(AppSettings.Folder, "settings.json"))); // и читаемо
    });
}

public class ScheduleTests
{
    [Fact]
    public void Rule_MainOn_SetsPowerTemperatureAndBrightness() => UiThread.Run(async () =>
    {
        await using var rig = await Rig.StartAsync(["off", "35", "3500", "off", "70", "1", "65280", "0", "0"]);

        await rig.Vm.RunRuleAsync(new ScheduleRule { Action = ScheduleAction.MainOn, Brightness = 40, Temperature = 2700 });

        await rig.WaitSentAsync("set_power", p => p[0].GetString() == "on");
        await rig.WaitSentAsync("set_ct_abx", p => p[0].GetInt32() == 2700);
        await rig.WaitSentAsync("set_bright", p => p[0].GetInt32() == 40);
    });

    [Fact]
    public void Rule_AllOff_TurnsBothOff() => UiThread.Run(async () =>
    {
        await using var rig = await Rig.StartAsync(["on", "35", "3500", "on", "70", "1", "65280", "0", "0"]);

        await rig.Vm.RunRuleAsync(new ScheduleRule { Action = ScheduleAction.AllOff });

        await rig.WaitSentAsync("set_power", p => p[0].GetString() == "off");
        await rig.WaitSentAsync("bg_set_power", p => p[0].GetString() == "off");
    });

    [Fact]
    public void AddRule_ValidatesAndSaves() => UiThread.Run(async () =>
    {
        await using var rig = await Rig.StartAsync();
        var vm = rig.Vm;

        vm.NewRule.TimeText = "25:00";
        vm.AddRuleCommand.Execute(null);
        Assert.NotEmpty(vm.NewRule.Error);
        Assert.Empty(vm.ScheduleItems);

        vm.NewRule.TimeText = "7:30";
        vm.AddRuleCommand.Execute(null);
        Assert.Empty(vm.NewRule.Error);
        var rule = Assert.Single(rig.Settings.Schedule);
        Assert.Equal((7, 30), (rule.Hour, rule.Minute));
        Assert.True(vm.HasUpcoming);

        vm.RemoveRuleCommand.Execute(vm.ScheduleItems.Single());
        Assert.Empty(rig.Settings.Schedule);
    });
}
