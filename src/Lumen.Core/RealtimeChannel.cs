namespace Lumen.Core;

/// <summary>
/// Канал для частых команд подсветки (синхронизация с экраном): без лимита ~60 команд/мин
/// и без ожидания ответов. Реализации — UDP-сессия (lamp15 и новее) и music mode (классические лампы).
/// </summary>
public interface IRealtimeChannel : IAsyncDisposable
{
    /// <summary>Название для статуса: «UDP» или «music mode».</summary>
    string Name { get; }

    /// <summary>Лампа умеет set_segment_rgb — отдельные цвета левой и правой половины подсветки.</summary>
    bool SupportsSegments { get; }

    Task SendAsync(string method, params object[] parameters);
}

public static class RealtimeChannel
{
    /// <summary>
    /// Открывает канал по списку возможностей лампы (поле support из discovery).
    /// Если список неизвестен — пробует UDP, затем music mode.
    /// </summary>
    public static async Task<IRealtimeChannel> OpenAsync(
        YeelightClient control, IReadOnlySet<string>? support, CancellationToken ct = default)
    {
        var tryUdp = support == null || support.Contains("udp_sess_new");
        var tryMusic = support == null || support.Contains("set_music");
        var segments = support?.Contains("set_segment_rgb") == true;

        YeelightException? error = null;

        if (tryUdp)
        {
            try { return await UdpRealtimeSession.StartAsync(control.Host, segments, ct).ConfigureAwait(false); }
            catch (YeelightException ex) { error = ex; }
        }

        if (tryMusic)
        {
            try { return await MusicModeSession.StartAsync(control, ct).ConfigureAwait(false); }
            catch (YeelightException ex) { error = ex; }
        }

        throw error ?? new YeelightException(
            "Лампа не поддерживает синхронизацию: нет ни UDP-сессии, ни music mode");
    }
}
