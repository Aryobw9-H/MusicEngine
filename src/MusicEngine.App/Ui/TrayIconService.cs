namespace MusicEngine.App.Ui;

using System.Windows;
using ViewModels;

/// <summary>
/// System tray icon (MVVM-08): Open / Cancel all downloads / Exit. Owns the
/// <see cref="System.Windows.Forms.NotifyIcon"/> lifecycle so the application
/// object no longer reaches into the ViewModel.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly MainViewModel _vm;
    private readonly Action _shutdown;
    private System.Windows.Forms.NotifyIcon? _icon;

    public TrayIconService(MainViewModel vm, Action shutdown)
    {
        _vm = vm;
        _shutdown = shutdown;
    }

    public void Attach(Window window)
    {
        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "MusicEngine",
            Visible = true,
        };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open MusicEngine", null, (_, _) => RestoreWindow(window));
        menu.Items.Add("Cancel all downloads", null, (_, _) => _vm.CancelAll());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) =>
        {
            if (_icon is not null) _icon.Visible = false;
            _shutdown();
        });
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => RestoreWindow(window);
    }

    private static void RestoreWindow(Window window)
    {
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }

    public void Dispose()
    {
        if (_icon is not null)
        {
            _icon.Visible = false;
            _icon.Dispose();
            _icon = null;
        }
    }
}
