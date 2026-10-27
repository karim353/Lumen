using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace Lumen.Views;

/// <summary>
/// Мини-панель из трея: быстрые переключатели и ползунки без большого окна и 3D-сцены.
/// Открывается левым кликом по иконке над панелью задач, прячется при клике мимо и по Esc.
/// </summary>
public partial class TrayFlyout : Window
{
    private long _hiddenAt;

    public TrayFlyout()
    {
        InitializeComponent();
        Deactivated += (_, _) => HidePanel();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) HidePanel(); };
    }

    /// <summary>«Открыть Lumen» — показать основное окно.</summary>
    public event Action? OpenRequested;

    /// <summary>Клик по иконке: показать или спрятать.</summary>
    public void Toggle()
    {
        if (IsVisible)
        {
            HidePanel();
            return;
        }
        // Клик по иконке сначала снимает фокус с панели (Deactivated её прячет), а потом приходит сюда —
        // без этой проверки панель тут же открылась бы снова
        if (Environment.TickCount64 - _hiddenAt < 350) return;
        ShowPanel();
    }

    private void ShowPanel()
    {
        // Рядом с треем: у края рабочей области, со стороны панели задач
        var area = SystemParameters.WorkArea;
        Show(); // размер известен только после показа (SizeToContent)
        UpdateLayout();

        // Панель задач сверху или слева — прижимаемся туда; снизу и справа — к правому нижнему углу
        var taskbarTop = area.Top > 0;
        var taskbarLeft = area.Left > 0;
        Left = taskbarLeft ? area.Left : area.Right - ActualWidth;
        Top = taskbarTop ? area.Top : area.Bottom - ActualHeight;

        Activate();
        Focus();

        if (!Motion.Enabled) return;
        // Выезжает к краю с панелью задач и проявляется
        var from = taskbarTop ? -14 : 14;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Shift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        Panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
    }

    private void HidePanel()
    {
        if (!IsVisible) return;
        _hiddenAt = Environment.TickCount64;
        Hide();
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        HidePanel();
        OpenRequested?.Invoke();
    }
}
