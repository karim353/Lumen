using System.Runtime.InteropServices;

namespace Lumen.Core;

/// <summary>Источник кадров для синхронизации.</summary>
public interface IScreenCapture : IDisposable
{
    /// <summary>«DXGI» или «GDI» — для строки статуса.</summary>
    string Name { get; }

    ScreenSample Sample(DisplayMonitor monitor);

    /// <summary>Копия последнего уменьшенного кадра (BGRA) — миниатюра экрана для 3D-сцены.</summary>
    FrameImage? CopyLastFrame();
}

/// <summary>Уменьшенный кадр экрана: BGRA, построчно, без отступов.</summary>
public sealed record FrameImage(byte[] Bgra, int Width, int Height);

public static class ScreenCapture
{
    /// <summary>DXGI Desktop Duplication, а если она недоступна (RDP, старый драйвер) — GDI.</summary>
    public static IScreenCapture Create()
    {
        try { return new DxgiScreenCapture(); }
        catch (Exception) { return new ScreenSampler(); }
    }
}

/// <summary>
/// Захват через DXGI Desktop Duplication: кадр не покидает видеокарту, она же уменьшает его
/// мип-уровнями (GenerateMips), а на CPU читается картинка ~60×33. На 4K это единицы миллисекунд
/// против 60–90 мс у GDI, которому приходится вычитывать из DWM весь экран.
/// COM вызывается напрямую через vtable — без SharpDX/Vortice.
/// </summary>
public sealed unsafe class DxgiScreenCapture : IScreenCapture
{
    private const int TargetW = 32, TargetH = 18; // минимальный размер уменьшенного кадра

    private nint _factory;
    private nint _device, _context;
    private nint _duplication;
    private nint _mipTex, _srv, _staging;
    private string? _deviceName;
    private int _width, _height, _mipLevel, _mipW, _mipH;
    private byte[] _pixels = [];
    private byte[] _raw = [];        // кадр FP16 до перевода в 8 бит
    private int _format;             // DXGI_FORMAT кадра: BGRA8 или FP16 (HDR)
    private double _sdrWhite = 1;    // белый SDR в единицах scRGB — для HDR
    private ScreenSample _last;
    private bool _hasFrame;
    private ScreenSampler? _gdi;
    private bool _usingGdi;
    private bool _unsupported;
    private bool _pixelsFromDxgi; // в _pixels лежит свежий кадр DXGI (а не пусто/устарело)
    private long _retryAfter;
    private bool _disposed;

    public DxgiScreenCapture()
    {
        var iid = IID_IDXGIFactory1;
        Check(CreateDXGIFactory1(&iid, out _factory), "CreateDXGIFactory1");
    }

    /// <summary>«GDI», пока DXGI не может отдать кадр (RDP, экран блокировки); «DXGI HDR» при включённом HDR.</summary>
    public string Name => _usingGdi ? "GDI" : _format == DXGI_FORMAT_R16G16B16A16_FLOAT ? "DXGI HDR" : "DXGI";

    public ScreenSample Sample(DisplayMonitor monitor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_unsupported)
        {
            if (_deviceName != monitor.DeviceName) ReleaseOutput();

            // После отказа (экран блокировки, UAC) не дёргаем DXGI каждый кадр
            if (_duplication == 0 && Environment.TickCount64 >= _retryAfter && !TryOpen(monitor.DeviceName))
                _retryAfter = Environment.TickCount64 + 1000;

            if (_duplication != 0)
            {
                // Первый кадр ждём чуть дольше: без изменений на экране новый кадр может не прийти
                var hr = AcquireFrame(_hasFrame ? 0u : 200u);
                if (hr is DXGI_ERROR_WAIT_TIMEOUT or S_NOT_UPDATED)
                {
                    if (!_hasFrame)
                    {
                        // Экран пока не менялся, а первый кадр дубликатора пустой (чёрный) — берём снимок через GDI
                        _gdi ??= new ScreenSampler();
                        _last = _gdi.Sample(monitor);
                        _hasFrame = true;
                    }
                    _usingGdi = false;
                    return _last; // экран не изменился
                }
                if (hr >= 0 && ReadFrame(monitor))
                {
                    _hasFrame = true;
                    _usingGdi = false;
                    return _last;
                }
                if (hr == E_FORMAT_UNSUPPORTED) _unsupported = true;

                ReleaseOutput(); // ACCESS_LOST: смена режима, полноэкранная игра, рабочий стол UAC
                _retryAfter = Environment.TickCount64 + 1000;
            }
        }

        _usingGdi = true;
        _pixelsFromDxgi = false;
        _gdi ??= new ScreenSampler();
        return _gdi.Sample(monitor);
    }

    public FrameImage? CopyLastFrame()
        => _pixelsFromDxgi && _pixels.Length > 0
            ? new FrameImage((byte[])_pixels.Clone(), _mipW, _mipH)
            : _gdi?.CopyLastFrame();

    private int AcquireFrame(uint timeoutMs)
    {
        var info = stackalloc byte[64]; // DXGI_OUTDUPL_FRAME_INFO (48 байт), нужен только LastPresentTime
        nint resource;
        var hr = Call(_duplication, 8, timeoutMs, (nint)info, &resource); // AcquireNextFrame
        if (hr < 0) return hr;

        try
        {
            // LastPresentTime == 0: изображение не обновлялось (сдвинулся только курсор, или это
            // самый первый кадр — он пустой). Копировать нечего.
            if (*(long*)info == 0) return S_NOT_UPDATED;

            nint tex;
            var iid = IID_ID3D11Texture2D;
            hr = QueryInterface(resource, &iid, &tex);
            if (hr < 0) return hr;
            try
            {
                if (_mipTex == 0) hr = CreateTextures(tex);
                if (hr < 0) return hr;

                // кадр → мип 0 → GenerateMips → маленький мип → staging
                CallVoid(_context, 46, _mipTex, 0u, 0u, 0u, 0u, tex, 0u, (nint)0); // CopySubresourceRegion
                CallVoid(_context, 54, _srv);                                       // GenerateMips
                CallVoid(_context, 46, _staging, 0u, 0u, 0u, 0u, _mipTex, (uint)_mipLevel, (nint)0);
            }
            finally
            {
                Release(tex);
            }
        }
        finally
        {
            Release(resource);
            Call(_duplication, 14); // ReleaseFrame
        }
        return 0;
    }

    private bool ReadFrame(DisplayMonitor monitor)
    {
        D3D11_MAPPED_SUBRESOURCE mapped;
        if (Call(_context, 14, _staging, 0u, 1u /*READ*/, 0u, (nint)(&mapped)) < 0) return false; // Map
        var hdr = _format == DXGI_FORMAT_R16G16B16A16_FLOAT;
        try
        {
            var row = _mipW * (hdr ? 8 : 4);
            fixed (byte* dst = hdr ? _raw : _pixels)
            {
                for (var y = 0; y < _mipH; y++)
                    Buffer.MemoryCopy((byte*)mapped.pData + y * mapped.RowPitch, dst + y * row, row, row);
            }
        }
        finally
        {
            CallVoid(_context, 15, _staging, 0u); // Unmap
        }
        if (hdr) HdrToBgra8(_raw, _pixels, _sdrWhite);
        // Повёрнутый монитор: кадр дубликатора не повёрнут — геометрию панели задач не применяем
        var rotated = (_width > _height) != (monitor.Bounds.Width > monitor.Bounds.Height);
        _last = ScreenSampler.Analyze(_pixels, _mipW, _mipH, rotated ? null : ScreenSampler.RegionFor(monitor, _mipW, _mipH));
        _pixelsFromDxgi = true;
        return true;
    }

    private int CreateTextures(nint frameTex)
    {
        D3D11_TEXTURE2D_DESC src;
        CallVoid(frameTex, 10, (nint)(&src)); // GetDesc
        if (src.Format is not (DXGI_FORMAT_B8G8R8A8_UNORM or DXGI_FORMAT_R16G16B16A16_FLOAT))
            return E_FORMAT_UNSUPPORTED; // 10-битные форматы и прочее — дальше только GDI
        _format = src.Format;

        _width = (int)src.Width;
        _height = (int)src.Height;

        // Самый маленький мип, который ещё не меньше 32×18
        _mipLevel = 0;
        while ((_width >> (_mipLevel + 1)) >= TargetW && (_height >> (_mipLevel + 1)) >= TargetH) _mipLevel++;
        _mipW = Math.Max(1, _width >> _mipLevel);
        _mipH = Math.Max(1, _height >> _mipLevel);
        _pixels = new byte[_mipW * _mipH * 4];
        _raw = _format == DXGI_FORMAT_R16G16B16A16_FLOAT ? new byte[_mipW * _mipH * 8] : [];

        var mipDesc = new D3D11_TEXTURE2D_DESC
        {
            Width = src.Width, Height = src.Height, MipLevels = (uint)_mipLevel + 1, ArraySize = 1,
            Format = _format, SampleCount = 1,
            Usage = 0 /*DEFAULT*/, BindFlags = 0x8 | 0x20 /*SHADER_RESOURCE | RENDER_TARGET*/,
            MiscFlags = 0x1 /*GENERATE_MIPS*/
        };
        nint mipTex, srv, staging;
        var hr = Call(_device, 5, (nint)(&mipDesc), (nint)0, (nint)(&mipTex)); // CreateTexture2D
        if (hr < 0) return hr;
        _mipTex = mipTex;

        hr = Call(_device, 7, _mipTex, (nint)0, (nint)(&srv)); // CreateShaderResourceView
        if (hr < 0) return hr;
        _srv = srv;

        var stagingDesc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)_mipW, Height = (uint)_mipH, MipLevels = 1, ArraySize = 1,
            Format = _format, SampleCount = 1,
            Usage = 3 /*STAGING*/, CPUAccessFlags = 0x20000 /*READ*/
        };
        hr = Call(_device, 5, (nint)(&stagingDesc), (nint)0, (nint)(&staging));
        if (hr < 0) return hr;
        _staging = staging;
        return 0;
    }

    /// <summary>Находит выход DXGI с нужным именем и создаёт устройство D3D11 на его видеокарте.</summary>
    private bool TryOpen(string deviceName)
    {
        for (uint a = 0; ; a++)
        {
            nint adapter;
            if (Call(_factory, 7, a, (nint)(&adapter)) < 0) return false; // EnumAdapters → NOT_FOUND
            try
            {
                for (uint o = 0; ; o++)
                {
                    nint output;
                    if (Call(adapter, 7, o, (nint)(&output)) < 0) break; // EnumOutputs
                    try
                    {
                        DXGI_OUTPUT_DESC desc;
                        if (Call(output, 7, (nint)(&desc)) < 0) continue; // GetDesc
                        if (new string(desc.DeviceName) != deviceName) continue;
                        return OpenDuplication(adapter, output, deviceName);
                    }
                    finally
                    {
                        Release(output);
                    }
                }
            }
            finally
            {
                Release(adapter);
            }
        }
    }

    private bool OpenDuplication(nint adapter, nint output, string deviceName)
    {
        if (_device == 0)
        {
            nint device, context;
            int level;
            var hr = D3D11CreateDevice(adapter, 0 /*UNKNOWN — задан адаптер*/, 0, 0x20 /*BGRA_SUPPORT*/,
                null, 0, 7 /*SDK_VERSION*/, &device, &level, &context);
            if (hr < 0) return false;
            _device = device;
            _context = context;
        }

        nint duplication;
        if (!TryDuplicateOutput1(output, out duplication))
        {
            nint output1;
            var iid = IID_IDXGIOutput1;
            if (QueryInterface(output, &iid, &output1) < 0) return false;
            try
            {
                if (Call(output1, 22, _device, (nint)(&duplication)) < 0) return false; // DuplicateOutput
            }
            finally
            {
                Release(output1);
            }
        }

        // Для HDR: насколько ярко Windows рисует белый обычных программ (ползунок «Яркость SDR»)
        _sdrWhite = DisplayConfig.SdrWhiteScale(deviceName) ?? DefaultSdrWhite;

        _duplication = duplication;
        _deviceName = deviceName;
        _hasFrame = false;
        return true;
    }

    /// <summary>
    /// IDXGIOutput5::DuplicateOutput1 (Windows 10 1803+): с ним при включённом HDR кадр приходит в FP16,
    /// а старый DuplicateOutput на HDR-мониторе отдаёт ошибку или искажённые цвета.
    /// </summary>
    private bool TryDuplicateOutput1(nint output, out nint duplication)
    {
        duplication = 0;
        nint output5;
        var iid = IID_IDXGIOutput5;
        if (QueryInterface(output, &iid, &output5) < 0) return false;
        try
        {
            var formats = stackalloc int[] { DXGI_FORMAT_R16G16B16A16_FLOAT, DXGI_FORMAT_B8G8R8A8_UNORM };
            nint dup;
            var hr = ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, int*, nint*, int>)Vt(output5, 26))(
                output5, _device, 0, 2, formats, &dup); // DuplicateOutput1
            if (hr < 0) return false;
            duplication = dup;
            return true;
        }
        finally
        {
            Release(output5);
        }
    }

    /// <summary>
    /// FP16 scRGB (линейный, 1.0 = 80 нит) → BGRA8 sRGB. Делим на белый SDR, чтобы обычный интерфейс
    /// был белым, а не «пересвеченным»; всё, что ярче, — просто максимум.
    /// </summary>
    internal static void HdrToBgra8(ReadOnlySpan<byte> rgba16f, Span<byte> bgra8, double sdrWhite)
    {
        var scale = 1 / Math.Max(0.1, sdrWhite);
        var pixels = Math.Min(rgba16f.Length / 8, bgra8.Length / 4);
        for (var i = 0; i < pixels; i++)
        {
            var src = rgba16f.Slice(i * 8, 8);
            var r = (double)BitConverter.UInt16BitsToHalf((ushort)(src[0] | src[1] << 8));
            var g = (double)BitConverter.UInt16BitsToHalf((ushort)(src[2] | src[3] << 8));
            var b = (double)BitConverter.UInt16BitsToHalf((ushort)(src[4] | src[5] << 8));
            bgra8[i * 4] = Encode(b * scale);
            bgra8[i * 4 + 1] = Encode(g * scale);
            bgra8[i * 4 + 2] = Encode(r * scale);
            bgra8[i * 4 + 3] = 255;
        }
    }

    private static byte Encode(double linear)
    {
        if (!(linear > 0)) return 0; // и NaN
        if (linear >= 1) return 255;
        var srgb = linear <= 0.0031308 ? 12.92 * linear : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
        return (byte)Math.Round(srgb * 255);
    }

    private void ReleaseOutput()
    {
        _pixelsFromDxgi = false;
        Release(_staging); _staging = 0;
        Release(_srv); _srv = 0;
        Release(_mipTex); _mipTex = 0;
        Release(_duplication); _duplication = 0;
        // Устройство привязано к видеокарте: другой монитор может быть на другой
        Release(_context); _context = 0;
        Release(_device); _device = 0;
        _deviceName = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseOutput();
        Release(_factory);
        _factory = 0;
        _gdi?.Dispose();
    }

    // ---- COM через vtable ----

    private static void* Vt(nint obj, int index) => (*(void***)obj)[index];

    private static int QueryInterface(nint obj, Guid* iid, nint* result)
        => ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Vt(obj, 0))(obj, iid, result);

    private static void Release(nint obj)
    {
        if (obj != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Vt(obj, 2))(obj);
    }

    private static int Call(nint obj, int i)
        => ((delegate* unmanaged[Stdcall]<nint, int>)Vt(obj, i))(obj);
    private static int Call(nint obj, int i, nint a)
        => ((delegate* unmanaged[Stdcall]<nint, nint, int>)Vt(obj, i))(obj, a);
    private static int Call(nint obj, int i, uint a, nint b)
        => ((delegate* unmanaged[Stdcall]<nint, uint, nint, int>)Vt(obj, i))(obj, a, b);
    private static int Call(nint obj, int i, nint a, nint b)
        => ((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)Vt(obj, i))(obj, a, b);
    private static int Call(nint obj, int i, nint a, nint b, nint c)
        => ((delegate* unmanaged[Stdcall]<nint, nint, nint, nint, int>)Vt(obj, i))(obj, a, b, c);
    private static int Call(nint obj, int i, uint a, nint b, nint* c)
        => ((delegate* unmanaged[Stdcall]<nint, uint, nint, nint*, int>)Vt(obj, i))(obj, a, b, c);
    private static int Call(nint obj, int i, nint a, uint b, uint c, uint d, nint e)
        => ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, nint, int>)Vt(obj, i))(obj, a, b, c, d, e);

    private static void CallVoid(nint obj, int i, nint a)
        => ((delegate* unmanaged[Stdcall]<nint, nint, void>)Vt(obj, i))(obj, a);
    private static void CallVoid(nint obj, int i, nint a, uint b)
        => ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)Vt(obj, i))(obj, a, b);
    private static void CallVoid(nint obj, int i, nint dst, uint dstSub, uint x, uint y, uint z, nint src, uint srcSub, nint box)
        => ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, uint, nint, uint, nint, void>)Vt(obj, i))(
            obj, dst, dstSub, x, y, z, src, srcSub, box);

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"{what}: 0x{hr:X8}");
    }

    private const int S_NOT_UPDATED = 1;
    private const int E_FORMAT_UNSUPPORTED = unchecked((int)0xA0000001); // свой код: формат кадра не BGRA8
    private const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
    private const int DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    private const int DXGI_FORMAT_R16G16B16A16_FLOAT = 10;
    private const double DefaultSdrWhite = 2.5; // 200 нит — если Windows не сообщила уровень

    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid IID_IDXGIOutput1 = new("00cddea8-939b-4b83-a340-a685226666cc");
    private static readonly Guid IID_IDXGIOutput5 = new("80a07424-ab52-42eb-833c-0c42fd282d98");
    private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_TEXTURE2D_DESC
    {
        public uint Width, Height, MipLevels, ArraySize;
        public int Format;
        public uint SampleCount, SampleQuality;
        public int Usage;
        public uint BindFlags, CPUAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_MAPPED_SUBRESOURCE
    {
        public void* pData;
        public uint RowPitch, DepthPitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_OUTPUT_DESC
    {
        public fixed char DeviceName[32];
        public int Left, Top, Right, Bottom;
        public int AttachedToDesktop;
        public int Rotation;
        public nint Monitor;
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(Guid* riid, out nint factory);

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags,
        int* featureLevels, uint numLevels, uint sdkVersion, nint* device, int* featureLevel, nint* context);
}
