using System.Windows.Controls;

namespace Lumen.Views.Pages;

// Разделы окна. Вся логика — в MainViewModel (DataContext наследуется от окна), здесь только разметка.

public partial class HomePage : UserControl
{
    public HomePage() => InitializeComponent();
}

public partial class DevicesPage : UserControl
{
    public DevicesPage() => InitializeComponent();
}

public partial class EffectsPage : UserControl
{
    public EffectsPage() => InitializeComponent();
}

public partial class SyncPage : UserControl
{
    public SyncPage() => InitializeComponent();
}

public partial class SchedulePage : UserControl
{
    public SchedulePage() => InitializeComponent();
}

public partial class SettingsPage : UserControl
{
    public SettingsPage() => InitializeComponent();
}
