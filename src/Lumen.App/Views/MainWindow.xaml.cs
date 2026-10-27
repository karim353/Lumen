using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Lumen.ViewModels;

namespace Lumen.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => OnViewModelChanged();
        SourceInitialized += (_, _) => FitToScreen();
        Loaded += (_, _) =>
        {
            if (TryFindResource("Rig") is LightRig rig && _vm != null) rig.Attach(this, _vm);
            StartAuraDrift();
            UpdatePreviewLoops();
        };
        SizeChanged += (_, _) => LayoutAura();
        // Свёрнуто или в трее — фоновые циклы на паузу
        IsVisibleChanged += (_, _) => Motion.SetPaused(!IsVisible || WindowState == WindowState.Minimized);
        StateChanged += (_, _) => Motion.SetPaused(!IsVisible || WindowState == WindowState.Minimized);
    }

    /// <summary>Вызывается при закрытии окна крестиком — вместо выхода прячемся в трей.</summary>
    public event System.Action? HiddenToTray;

    /// <summary>true — окно реально закрывается (выход из приложения).</summary>
    public bool AllowClose { get; set; }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
            HiddenToTray?.Invoke();
        }
        base.OnClosing(e);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    public void ShowFromTray()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;  // гарантированно поверх остальных окон
        Topmost = false;
        Focus();
    }

    /// <summary>Дашборд рассчитан на 1480×960; на экране поменьше окно ужимается до 92 % рабочей области.</summary>
    private void FitToScreen()
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Max(MinWidth, Math.Min(Width, area.Width * 0.92));
        Height = Math.Max(MinHeight, Math.Min(Height, area.Height * 0.92));
    }

    private void OnViewModelChanged()
    {
        if (_vm != null) _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm = DataContext as MainViewModel;
        if (_vm != null) _vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentPage)) UpdatePreviewLoops();
    }

    /// <summary>Превью эффектов крутятся, только пока открыта страница «Эффекты».</summary>
    private void UpdatePreviewLoops()
        => Motion.SetGroupPaused(Motion.PreviewGroup, _vm?.CurrentPage != AppPage.Effects);

    // ---------- Свечение окна ----------

    private void LayoutAura()
    {
        var w = ActualWidth;
        var h = ActualHeight;
        Place(AuraLeft, -0.2 * w, -0.25 * h, 0.8 * w, 1.0 * h);
        Place(AuraRight, 0.35 * w, -0.15 * h, 0.8 * w, 1.0 * h);
        Place(AuraMain, 0.1 * w, 0.45 * h, 0.9 * w, 0.8 * h);
    }

    private static void Place(FrameworkElement e, double x, double y, double width, double height)
    {
        e.Width = width;
        e.Height = height;
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
    }

    /// <summary>Пятна света медленно плывут с разными периодами — фон «живой», но не отвлекает.</summary>
    private void StartAuraDrift()
    {
        Drift(AuraLeft, 90, 60, 19);
        Drift(AuraRight, -80, 80, 23);
        Drift(AuraMain, 70, -50, 29);
    }

    /// <summary>
    /// Пятна смещаются на доли пикселя в секунду — 12 кадров хватает, а каждый кадр дрейфа
    /// перерисовывает окно под карточками.
    /// </summary>
    private static void Drift(UIElement e, double dx, double dy, double seconds)
    {
        var shift = new TranslateTransform();
        e.RenderTransform = shift;
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        Motion.RunLoop(shift, TranslateTransform.XProperty, new DoubleAnimation(-dx / 2, dx / 2, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease
        }, frameRate: 12);
        Motion.RunLoop(shift, TranslateTransform.YProperty, new DoubleAnimation(-dy / 2, dy / 2, TimeSpan.FromSeconds(seconds * 1.37))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease
        }, frameRate: 12);
    }
}
