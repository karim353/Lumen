using System;
using Lumen.ViewModels;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Lumen.Services;

/// <summary>Иконка в трее с тёмным меню: быстрые переключатели без открытия окна.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu;

    /// <param name="toggleFlyout">Левый клик — мини-панель; двойной — основное окно; правый — меню.</param>
    public TrayIcon(MainViewModel vm, Action showWindow, Action toggleFlyout, Action exit)
    {
        var font = new Drawing.Font("Segoe UI", 10f);

        var open = new Forms.ToolStripMenuItem("Открыть Lumen", null, (_, _) => showWindow())
        {
            Font = new Drawing.Font(font, Drawing.FontStyle.Bold)
        };
        var main = new Forms.ToolStripMenuItem("Основной свет", null, (_, _) => vm.MainPower = !vm.MainPower);
        var bg = new Forms.ToolStripMenuItem("Подсветка", null, (_, _) => vm.BgPower = !vm.BgPower);
        var sync = new Forms.ToolStripMenuItem("Синхронизация с экраном", null,
            (_, _) => vm.ToggleSyncCommand.Execute(null));
        var quit = new Forms.ToolStripMenuItem("Выход", null, (_, _) => exit());

        _menu = new Forms.ContextMenuStrip
        {
            Renderer = new DarkRenderer(),
            ShowImageMargin = false,
            ShowCheckMargin = true,
            Font = font,
            Padding = new Forms.Padding(4)
        };
        _menu.Items.AddRange(new Forms.ToolStripItem[]
        {
            open, new Forms.ToolStripSeparator(), main, bg, sync, new Forms.ToolStripSeparator(), quit
        });
        _menu.Opening += (_, _) =>
        {
            main.Checked = vm.MainPower;
            bg.Checked = vm.BgPower;
            sync.Checked = vm.IsSyncing;
            main.Enabled = bg.Enabled = vm.IsConnected;
            sync.Enabled = vm.IsConnected || vm.IsSyncing;
        };

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Lumen",
            ContextMenuStrip = _menu,
            Visible = true
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) toggleFlyout();
        };
        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) showWindow();
        };
    }

    public void ShowHint(string title, string text)
        => _icon.ShowBalloonTip(3000, title, text, Forms.ToolTipIcon.None);

    private static Drawing.Icon LoadIcon()
    {
        var res = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/lumen.ico"));
        return res != null
            ? new Drawing.Icon(res.Stream, Forms.SystemInformation.SmallIconSize)
            : Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }

    // ---------- Тёмная отрисовка меню ----------

    private sealed class DarkColors : Forms.ProfessionalColorTable
    {
        private static readonly Drawing.Color Bg = Drawing.Color.FromArgb(0x1A, 0x1A, 0x1A);
        private static readonly Drawing.Color Hover = Drawing.Color.FromArgb(0x2C, 0x2C, 0x2C);
        private static readonly Drawing.Color Line = Drawing.Color.FromArgb(0x33, 0x33, 0x33);

        public override Drawing.Color ToolStripDropDownBackground => Bg;
        public override Drawing.Color ImageMarginGradientBegin => Bg;
        public override Drawing.Color ImageMarginGradientMiddle => Bg;
        public override Drawing.Color ImageMarginGradientEnd => Bg;
        public override Drawing.Color MenuBorder => Line;
        public override Drawing.Color MenuItemBorder => Hover;
        public override Drawing.Color MenuItemSelected => Hover;
        public override Drawing.Color MenuItemSelectedGradientBegin => Hover;
        public override Drawing.Color MenuItemSelectedGradientEnd => Hover;
        public override Drawing.Color SeparatorDark => Line;
        public override Drawing.Color SeparatorLight => Bg;
        public override Drawing.Color CheckBackground => Bg;
        public override Drawing.Color CheckSelectedBackground => Hover;
        public override Drawing.Color CheckPressedBackground => Hover;
    }

    private sealed class DarkRenderer : Forms.ToolStripProfessionalRenderer
    {
        private static readonly Drawing.Color TextColor = Drawing.Color.FromArgb(0xED, 0xED, 0xED);
        private static readonly Drawing.Color DisabledColor = Drawing.Color.FromArgb(0x5E, 0x5E, 0x5E);

        public DarkRenderer() : base(new DarkColors()) => RoundedEdges = false;

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? TextColor : DisabledColor;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
        {
            Forms.TextRenderer.DrawText(e.Graphics, "✓", e.Item.Font, e.ImageRectangle,
                e.Item.Enabled ? TextColor : DisabledColor,
                Forms.TextFormatFlags.HorizontalCenter | Forms.TextFormatFlags.VerticalCenter);
        }
    }
}
