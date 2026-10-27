using System.Runtime.InteropServices;

namespace Lumen.Core;

/// <summary>
/// Сведения о HDR монитора из Windows Display Config API: включён ли HDR и насколько ярким
/// Windows рисует «белый» обычных (SDR) программ — ползунок «Яркость SDR-содержимого».
/// </summary>
internal static unsafe class DisplayConfig
{
    /// <summary>
    /// Белый SDR в единицах scRGB (1.0 = 80 нит), например 2.5 = 200 нит.
    /// По нему кадр HDR приводится к обычной шкале: интерфейс — белый, яркие блики — «пересвет».
    /// </summary>
    public static double? SdrWhiteScale(string gdiDeviceName)
    {
        var target = FindTarget(gdiDeviceName);
        if (target == null) return null;

        var req = new SdrWhiteLevel { Header = Header(DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL, sizeof(SdrWhiteLevel), target.Value) };
        if (DisplayConfigGetDeviceInfo(&req) != 0 || req.Level == 0) return null;
        return req.Level / 1000.0;
    }

    /// <summary>Включён ли HDR («Использовать HDR» в параметрах дисплея).</summary>
    public static bool? IsHdrEnabled(string gdiDeviceName)
    {
        var target = FindTarget(gdiDeviceName);
        if (target == null) return null;

        var req = new AdvancedColorInfo { Header = Header(DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO, sizeof(AdvancedColorInfo), target.Value) };
        if (DisplayConfigGetDeviceInfo(&req) != 0) return null;
        return (req.Value & 0x2) != 0; // advancedColorEnabled
    }

    private readonly record struct Target(uint AdapterLow, int AdapterHigh, uint Id);

    /// <summary>Активный путь «источник → монитор», у которого источник — нужный \\.\DISPLAYn.</summary>
    private static Target? FindTarget(string gdiDeviceName)
    {
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount) != 0) return null;

        var paths = new byte[pathCount * PathInfoSize];
        var modes = new byte[modeCount * ModeInfoSize];
        fixed (byte* pp = paths, pm = modes)
        {
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, pp, ref modeCount, pm, 0) != 0) return null;

            for (var i = 0; i < pathCount; i++)
            {
                var path = pp + i * PathInfoSize;
                // DISPLAYCONFIG_PATH_INFO: sourceInfo { LUID adapterId; UINT32 id; … } (20 байт),
                // затем targetInfo { LUID adapterId; UINT32 id; … }
                var source = new Target(*(uint*)path, *(int*)(path + 4), *(uint*)(path + 8));
                var target = new Target(*(uint*)(path + 20), *(int*)(path + 24), *(uint*)(path + 28));

                var name = new SourceDeviceName { Header = Header(DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME, sizeof(SourceDeviceName), source) };
                if (DisplayConfigGetDeviceInfo(&name) != 0) continue;
                if (new string(name.ViewGdiDeviceName) == gdiDeviceName) return target;
            }
        }
        return null;
    }

    private static DeviceInfoHeader Header(int type, int size, Target t)
        => new() { Type = type, Size = (uint)size, AdapterLow = t.AdapterLow, AdapterHigh = t.AdapterHigh, Id = t.Id };

    private const uint QDC_ONLY_ACTIVE_PATHS = 2;
    private const int PathInfoSize = 72, ModeInfoSize = 64;
    private const int DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const int DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 9;
    private const int DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL = 11;

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoHeader
    {
        public int Type;
        public uint Size;
        public uint AdapterLow;
        public int AdapterHigh;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceDeviceName
    {
        public DeviceInfoHeader Header;
        public fixed char ViewGdiDeviceName[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SdrWhiteLevel
    {
        public DeviceInfoHeader Header;
        public uint Level; // 1000 = 80 нит
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdvancedColorInfo
    {
        public DeviceInfoHeader Header;
        public uint Value;
        public int ColorEncoding;
        public uint BitsPerColorChannel;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPaths, byte* paths, ref uint numModes, byte* modes, nint topology);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(void* packet);
}
