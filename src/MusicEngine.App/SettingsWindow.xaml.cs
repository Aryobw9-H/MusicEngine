namespace MusicEngine.App;

using System.Windows;
using Configuration;
using ViewModels;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    /// <summary>Designer-only. Production windows construct with a SettingsViewModel.</summary>
    public SettingsWindow() : this(new SettingsViewModel(AppConfig.Load())) { }

    private void Save_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
