namespace Lumen.Core;

/// <summary>
/// Пропускает не чаще одного действия за интервал и всегда выполняет последнее.
/// Нужен для слайдеров: при перетаскивании лампа получит ~3 команды в секунду,
/// а не сотню (у Yeelight лимит ~60 команд в минуту на соединение).
/// Если лимит всё же исчерпан (<see cref="YeelightQuotaException"/>), последнее значение
/// повторяется с нарастающей паузой — иначе лампа и слайдер разошлись бы.
/// </summary>
public sealed class Throttler(TimeSpan interval)
{
    private readonly object _lock = new();
    private Func<Task>? _pending;
    private bool _running;
    private DateTime _last = DateTime.MinValue;

    /// <summary>Минимальный промежуток между действиями. Можно менять на ходу (UDP — чаще, TCP — реже).</summary>
    public TimeSpan Interval { get; set; } = interval;

    /// <summary>Первая пауза после отказа по лимиту; дальше удваивается до <see cref="MaxQuotaBackoff"/>.</summary>
    public TimeSpan QuotaBackoff { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaxQuotaBackoff { get; init; } = TimeSpan.FromSeconds(16);

    /// <summary>Лимит исчерпан (true) или значение снова дошло до лампы (false).</summary>
    public event Action<bool>? QuotaStateChanged;

    public void Run(Func<Task> action)
    {
        lock (_lock)
        {
            _pending = action;
            if (_running) return;
            _running = true;
        }
        _ = LoopAsync();
    }

    private async Task LoopAsync()
    {
        var backoff = TimeSpan.Zero;
        while (true)
        {
            var wait = backoff > TimeSpan.Zero ? backoff : Interval - (DateTime.UtcNow - _last);
            if (wait > TimeSpan.Zero) await Task.Delay(wait).ConfigureAwait(false);

            Func<Task>? action;
            lock (_lock)
            {
                action = _pending;
                _pending = null;
                if (action == null)
                {
                    _running = false;
                    return;
                }
            }

            _last = DateTime.UtcNow;
            try
            {
                await action().ConfigureAwait(false);
                if (backoff > TimeSpan.Zero) QuotaStateChanged?.Invoke(false);
                backoff = TimeSpan.Zero;
            }
            catch (YeelightQuotaException)
            {
                // Повторяем это же значение, если пользователь не успел выбрать новое
                lock (_lock) _pending ??= action;
                if (backoff == TimeSpan.Zero) QuotaStateChanged?.Invoke(true);
                backoff = backoff == TimeSpan.Zero
                    ? QuotaBackoff
                    : TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxQuotaBackoff.Ticks));
            }
            catch { /* остальные ошибки обрабатывает само действие */ }
        }
    }
}
